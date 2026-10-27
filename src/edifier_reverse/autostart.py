"""開機自動啟動：在目前使用者的 Run 機碼寫一筆。只在使用者從選單切換時才改，不需要系統管理員權限。

機碼路徑、名稱與指令都可注入，測試用獨立路徑，不會動到真正的開機項目。
"""
import sys
from pathlib import Path
from typing import Optional

DEFAULT_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
VALUE_NAME = "EdifierReverseTray"


def default_command() -> str:
    """用 pythonw（沒有主控台視窗）執行 `-m edifier_reverse tray`；找不到 pythonw 時退回目前的直譯器。
    路徑加引號，含空白也安全。"""
    exe = Path(sys.executable)
    pyw = exe.with_name("pythonw.exe")
    return f'"{pyw if pyw.exists() else exe}" -m edifier_reverse tray'


def is_enabled(key: str = DEFAULT_KEY, name: str = VALUE_NAME) -> bool:
    try:
        import winreg

        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, key) as k:
            return isinstance(winreg.QueryValueEx(k, name)[0], str)
    except OSError:
        return False


def set_enabled(enabled: bool, key: str = DEFAULT_KEY, name: str = VALUE_NAME,
                command: Optional[str] = None) -> None:
    import winreg

    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, key) as k:
        if enabled:
            winreg.SetValueEx(k, name, 0, winreg.REG_SZ, command or default_command())
        else:
            try:
                winreg.DeleteValue(k, name)
            except FileNotFoundError:
                pass  # 本來就沒有，重複停用不算錯
