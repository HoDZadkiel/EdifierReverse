import gc
import time

import pytest


@pytest.fixture(autouse=True)
def isolated_appdata(tmp_path, monkeypatch):
    """設定檔與裝置清單存在 %APPDATA%；測試一律用暫存目錄，不能讀寫到使用者真正的設定。"""
    monkeypatch.setenv("APPDATA", str(tmp_path / "appdata"))


@pytest.fixture
def tk_root():
    """Tk 根視窗。建立偶爾（約六分之一）會失敗，所以重試幾次；仍失敗才跳過，並在原因裡附上錯誤訊息。"""
    import tkinter as tk

    last = None
    for _ in range(5):
        try:
            root = tk.Tk()
            break
        except tk.TclError as e:
            last = e
            time.sleep(0.3)
    else:
        pytest.skip(f"沒有可用的顯示環境：{last}")
    root.withdraw()
    yield root
    root.destroy()
    # Tk 物件（根視窗、圖片、字型）常在循環參照裡，要等垃圾回收才釋放。若回收剛好發生在別的執行緒
    # （例如之後測試的 asyncio executor），Tcl 會因「被錯的執行緒刪除」直接讓直譯器崩潰（0x80000003）。
    # 所以在主執行緒當場回收掉。
    del root
    gc.collect()
