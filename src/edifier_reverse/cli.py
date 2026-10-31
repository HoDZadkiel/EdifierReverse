"""命令列：scan（掃描）、probe（實機只讀探測）、sim（離線自測）、status／anc（裝置服務）。

probe 不送任何設定類指令；唯一會改變裝置狀態的是 anc 子命令（0xC1，經節流）。"""
import argparse
import asyncio
import json
import socket
import threading
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import List, Optional

from .client import Client
from .packet import Session
from .probe import probe


class PlanError(ValueError):
    pass


@dataclass
class Plan:
    transport: str  # "ble" | "spp"
    session: Session
    target: str     # ble：BLE 位址；spp：A2DP（經典藍牙）位址
    warnings: List[str] = field(default_factory=list)


def resolve_transport(model: str, transport: str):
    """決定走哪條傳輸，回傳 (傳輸, 警告清單)。transport = "auto" 時依官方 App 的規則：
    產品表有 SPP UUID 就走 SPP，否則走 BLE。不可能的組合直接報錯，讓呼叫端在碰藍牙之前就能失敗。"""
    from .ble import PROFILES

    profile = PROFILES[model]
    t = profile.default_transport if transport == "auto" else transport
    if t == "spp" and not profile.spp:
        raise PlanError(f"{model} 在產品表中沒有 SPP UUID，官方 App 對它只走 BLE")
    warnings = []
    if t == "ble" and profile.spp:
        warnings.append(f"官方 App 對 {model} 走 SPP；BLE GATT 這條路只是產品表有列，未必可用")
    return t, warnings


def plan_connection(model: str, transport: str, advert, address: Optional[str],
                    a2dp_address: Optional[str], version: Optional[int],
                    encryption: int) -> Plan:
    """決定走哪條傳輸、用什麼 Session、連哪個位址。不碰藍牙，方便單獨測試。"""
    t, warnings = resolve_transport(model, transport)

    if version is not None:
        session = Session(version, encryption)
    elif advert is not None:
        session = advert.session
    else:
        session = Session()
        warnings.append("找不到廣播資料，改用預設 Session(version=2, encryption=0)")

    if t == "ble":
        target = address or (advert.ble_address if advert else None)
        if not target:
            raise PlanError("找不到 BLE 位址：沒有掃到符合的廣播，也沒有用 --address 指定")
    else:
        target = a2dp_address or (advert.a2dp_address if advert else None)
        if not target:
            raise PlanError("找不到 A2DP 位址：沒有掃到符合的廣播，也沒有用 --a2dp-address 指定")
    return Plan(t, session, target, warnings)


def _print(results) -> None:
    for r in results:
        print(f"[0x{r['cmd']:02X}] {r['name']}")
        print(f"    送出：{r['request']}")
        print(f"    回應：{r['response_payload']}")
        print(f"    解析：{r['parsed']}")


def _save(results, path: Path, meta: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8") as f:
        f.write(json.dumps({"meta": meta}, ensure_ascii=False) + "\n")
        for r in results:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")
    print(f"\n已寫入 {path}（含裝置 MAC，已在 .gitignore 內，勿 push）")


async def _probe_real(args) -> None:
    from .ble import PROFILES, BleakTransport, discover
    from .spp import connect_rfcomm

    profile = PROFILES[args.model]
    resolve_transport(args.model, args.transport)  # 不可能的組合在掃描之前就報錯
    advert = None
    if args.version is None or not (args.address or args.a2dp_address):
        print("掃描廣播 ...")
        advert = await discover(profile, args.address)
        if advert:
            print(f"找到 {advert.ble_address}  A2DP={advert.a2dp_address}  rssi={advert.rssi}")
    plan = plan_connection(args.model, args.transport, advert, args.address,
                           args.a2dp_address, args.version, args.encryption)
    for w in plan.warnings:
        print(f"警告：{w}")
    print(f"傳輸={plan.transport}  目標={plan.target}  "
          f"version={plan.session.version} encryption={plan.session.encryption}")

    if plan.transport == "ble":
        transport = await BleakTransport.connect(plan.target, profile)
        how = "BLE GATT"
    else:
        transport = await connect_rfcomm(plan.target, args.channel)
        how = transport.how
    print(f"連線方式：{how}")
    try:
        results = await probe(Client(transport, plan.session))
    finally:
        await transport.close()
    _print(results)
    stamp = time.strftime("%Y%m%d-%H%M%S")
    _save(results, Path(args.log or f"docs/captures/probe-{args.model}-{stamp}.jsonl"),
          {"model": args.model, "transport": plan.transport, "how": how,
           "version": plan.session.version, "encryption": plan.session.encryption})


async def _probe_sim(args) -> None:
    from .sim import SimulatedDevice, serve_socket
    from .spp import SocketTransport

    session = Session(args.version or 2, args.encryption or 0)
    dev = SimulatedDevice(session, chunk_size=args.chunk)
    if not args.spp:
        _print(await probe(Client(dev, session), timeout=0.5))
        return
    # 用真的 socket 對跑：測 SocketTransport 的執行緒與串流處理
    app_side, dev_side = socket.socketpair()
    threading.Thread(target=serve_socket, args=(dev, dev_side), daemon=True).start()
    transport = SocketTransport(app_side)
    try:
        _print(await probe(Client(transport, session), timeout=0.5))
    finally:
        await transport.close()
        dev_side.close()


def _print_status(state) -> None:
    print(f"裝置：{state.name}  韌體 {state.version}")
    print(f"電量：{state.battery}%")
    print(f"ANC：{state.anc_label}" + (f"（強度 {state.anc.db}）" if state.anc and state.anc.db else ""))


async def run_service(connector, args) -> None:
    """status／anc 的共同流程：連線 -> 印狀態 -> （anc）設定並印結果。connector 可注入，方便離線測試。"""
    from .service import DeviceService, ServiceError

    svc = DeviceService(connector)
    svc.start()
    try:
        _print_status(await svc.wait_connected(args.timeout))
        if args.cmd == "anc":
            new = await svc.set_anc(args.mode, args.db)
            print(f"已切換：{svc.state.anc_label}" + (f"（強度 {new.db}）" if args.db else ""))
    except ServiceError as e:
        raise SystemExit(f"錯誤：{e}")
    finally:
        await svc.stop()


async def run_power_save(connector, args) -> None:
    """讀取省電模式；--set 時改成指定值並讀回確認。設定前先印出原值，方便還原。connector 可注入，方便離線測試。"""
    from . import commands as C
    from .client import Client, ResponseTimeout
    from .requests import query, set_power_save
    from .responses import parse_power_save

    async def read(client) -> bool:
        return parse_power_save((await client.request(query(C.SAVING_MODE_QUERY))).payload)

    transport, session = await connector()
    client = Client(transport, session, timeout=args.timeout)
    try:
        try:
            before = await read(client)
        except ResponseTimeout:
            raise SystemExit("錯誤：裝置沒有回應省電模式查詢，這個機種可能沒有此設定")
        print(f"省電模式：{'開' if before else '關'}")
        if args.set is None:
            return
        want = args.set == "on"
        if want == before:
            print("已經是這個值，不送出任何設定")
            return
        await client.request(set_power_save(want))
        after = await read(client)
        print(f"已設定：{'開' if before else '關'} → {'開' if after else '關'}"
              f"（要還原：power-save --set {'on' if before else 'off'} --yes）")
        if after != want:
            raise SystemExit("錯誤：讀回的值與設定不同，裝置可能沒有採用")
    finally:
        await transport.close()


async def _list_devices() -> None:
    from .ble import discover_all, profile_for

    found = await discover_all(8.0)
    for a in found:
        transport = "SPP" if profile_for(a.model).spp else "BLE"
        print(f"{a.model:<14} A2DP={a.a2dp_address}  BLE={a.ble_address}  rssi={a.rssi}  "
              f"version={a.session.version} encryption={a.session.encryption}  傳輸={transport}")
    print(f"\n{len(found)} 個 Edifier 裝置")


def main(argv=None) -> None:
    ap = argparse.ArgumentParser(prog="edifier_reverse")
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("devices", help="掃描並列出附近所有被辨識為 Edifier 的裝置（官方 App 的判定方式，只讀）")
    sc = sub.add_parser("scan", help="掃描 BLE 廣播（只讀）")
    sc.add_argument("--seconds", type=float, default=10.0)
    sc.add_argument("--all", action="store_true", help="列出所有裝置")

    pr = sub.add_parser("probe", help="連線並送出白名單內的查詢指令")
    pr.add_argument("--model", choices=["ES850NB", "MP330"], required=True)
    pr.add_argument("--transport", choices=["auto", "ble", "spp"], default="auto",
                    help="auto：依官方 App 規則（ES850NB 走 SPP，MP330 走 BLE）")
    pr.add_argument("--address", help="BLE 位址（限定掃描結果，或 BLE 連線目標）")
    pr.add_argument("--a2dp-address", help="經典藍牙位址（SPP 連線目標），預設取自廣播資料")
    pr.add_argument("--channel", type=int,
                    help="直連指定的 RFCOMM channel。不指定時照官方順序：先用 UUID 查詢，失敗再試 channel 1")
    pr.add_argument("--version", type=int, help="直接指定 bleVersion，略過廣播推導")
    pr.add_argument("--encryption", type=int, default=0)
    pr.add_argument("--log", help="輸出 jsonl 路徑，預設 docs/captures/")

    si = sub.add_parser("sim", help="對離線模擬裝置跑一次探測（自測用）")
    si.add_argument("--version", type=int)
    si.add_argument("--encryption", type=int)
    si.add_argument("--chunk", type=int, help="把回應切成每片 N 位元組")
    si.add_argument("--spp", action="store_true", help="改用真的 socket 對跑，測 SPP 傳輸層")

    ps = sub.add_parser("power-save", help="讀取（或用 --set 改）裝置的省電模式；關閉可避免閒置自動關機／待機")
    ps.add_argument("--set", choices=["on", "off"], help="改成指定值（會改變裝置的設定，須同時加 --yes）")
    ps.add_argument("--yes", action="store_true", help="確認要改變裝置設定")
    ps.add_argument("--model", help="指定機種（例如 MP330）；多台裝置同時連著時用來挑對象，不會改動設定檔的選用裝置")
    ps.add_argument("--timeout", type=float, default=5.0, help="等待回應的秒數")

    sub.add_parser("tray", help="啟動托盤小工具（需要 uv sync --extra tray）")

    st = sub.add_parser("status", help="連線並印出名稱、韌體、電量、ANC 狀態（只讀）")
    an = sub.add_parser("anc", help="切換 ANC 模式（會改變耳機狀態；目前只支援 group 16）")
    for p in (st, an):
        p.add_argument("--model", choices=["ES850NB"], default="ES850NB")
        p.add_argument("--timeout", type=float, default=40.0, help="等待連線的秒數")
    an.add_argument("mode", type=int, choices=[1, 2, 3, 4, 5],
                    help="1 深度降噪 2 舒適降噪 3 防風噪 4 可調增益環境音 5 降噪關")
    an.add_argument("--db", type=int, help="環境音強度 1–12，只有 mode 4 可用")

    args = ap.parse_args(argv)
    if args.cmd == "devices":
        asyncio.run(_list_devices())
    elif args.cmd == "tray":
        try:
            from .tray import main as tray_main
        except ImportError as e:
            raise SystemExit(f"錯誤：缺少選用相依（{e}）。請執行 uv sync --extra tray")
        tray_main()
    elif args.cmd == "power-save":
        if args.set and not args.yes:
            raise SystemExit("錯誤：--set 會改變裝置設定，請加上 --yes 確認")
        from .devices import DeviceDirectory

        directory = DeviceDirectory(save=lambda _s: None)  # 不寫回設定檔
        if args.model:
            asyncio.run(directory.add_paired())  # 把 Windows 已配對的 Edifier 裝置補進（記憶體內）清單
            dev = next((d for d in directory.settings.devices if d.model.upper() == args.model.upper()), None)
            if dev is None:
                raise SystemExit(f"錯誤：設定檔裡沒有 {args.model}，請確認它已在 Windows 配對")
            directory.settings.selected = dev.a2dp  # 只在記憶體
        asyncio.run(run_power_save(directory.connector(), args))
    elif args.cmd in ("status", "anc"):
        from .devices import make_connector
        asyncio.run(run_service(make_connector(args.model), args))
    elif args.cmd == "scan":
        from .ble import scan
        asyncio.run(scan(args.seconds, args.all))
    elif args.cmd == "probe":
        try:
            asyncio.run(_probe_real(args))
        except PlanError as e:
            raise SystemExit(f"錯誤：{e}")
    else:
        asyncio.run(_probe_sim(args))


if __name__ == "__main__":
    main()
