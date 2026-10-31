"""省電模式（0xB1／0xB2）與無音訊自動關機（0xD6／0xD7）：封包與解析，以及 CLI 的安全規則。
指令格式來自官方 App ConneX 1.0.31 的反編譯碼（CommandManager.setPowerSaveMode／setAutoShutdownNoInput），尚未實機驗證。"""
import argparse
import asyncio

import pytest

from edifier_reverse import commands as C
from edifier_reverse.cli import main, run_power_save
from edifier_reverse.packet import PacketError, Session, build
from edifier_reverse.requests import set_auto_shutdown_no_input, set_power_save
from edifier_reverse.responses import parse_auto_shutdown_no_input, parse_power_save
from edifier_reverse.sim import SimulatedDevice

S = Session(2, 0)  # MP330 實機：版本 2、不加密


def test_set_power_save_is_one_byte_flag_on_the_saving_mode_command():
    assert (C.SAVING_MODE_QUERY, C.SAVING_MODE_SET) == (0xB1, 0xB2)
    assert set_power_save(False).to_bytes(S) == build(S, 0xB2, b"\x00")
    assert set_power_save(True).to_bytes(S) == build(S, 0xB2, b"\x01")


def test_auto_shutdown_no_input_uses_its_own_command_codes():
    assert (C.AUTO_SHUTDOWN_WITHOUT_AUDIO_QUERY, C.AUTO_SHUTDOWN_WITHOUT_AUDIO_SET) == (0xD7, 0xD6)
    assert set_auto_shutdown_no_input(False).to_bytes(S) == build(S, 0xD6, b"\x00")


@pytest.mark.parametrize("parse", [parse_power_save, parse_auto_shutdown_no_input])
def test_responses_are_true_only_for_1_and_empty_is_an_error(parse):
    assert parse(b"\x01") is True and parse(b"\x00") is False and parse(b"\x02") is False   # 與官方 App 一樣只認 1
    with pytest.raises(PacketError):
        parse(b"")


def _run(set_value=None):
    dev = SimulatedDevice(S)
    dev.state[C.SAVING_MODE_QUERY] = b"\x01"

    async def connector():
        return dev, S

    args = argparse.Namespace(set=set_value, yes=True, timeout=1.0)
    asyncio.run(run_power_save(connector, args))
    return dev


def test_read_only_run_sends_just_the_query(capsys):
    dev = _run()
    assert "省電模式：開" in capsys.readouterr().out
    assert [w[2] for w in dev.written] == [C.SAVING_MODE_QUERY]   # 沒有任何設定指令


def test_set_off_reports_the_previous_value_reads_back_and_shows_how_to_restore(capsys):
    dev = _run("off")
    out = capsys.readouterr().out
    assert "開 → 關" in out and "power-save --set on --yes" in out
    assert [w[2] for w in dev.written] == [C.SAVING_MODE_QUERY, C.SAVING_MODE_SET, C.SAVING_MODE_QUERY]
    assert dev.state[C.SAVING_MODE_QUERY] == b"\x00"


def test_setting_the_value_it_already_has_sends_nothing(capsys):
    dev = _run("on")
    assert [w[2] for w in dev.written] == [C.SAVING_MODE_QUERY]
    assert "不送出任何設定" in capsys.readouterr().out


def test_cli_refuses_to_change_the_device_without_yes():
    with pytest.raises(SystemExit, match="--yes"):
        main(["power-save", "--set", "off"])
