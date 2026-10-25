# Fixture: Python tool using ctypes injection and process enumeration.
import ctypes
ctypes.windll.user32.SendInput(1, None, 0)
ctypes.windll.kernel32.CreateToolhelp32Snapshot(2, 0)
