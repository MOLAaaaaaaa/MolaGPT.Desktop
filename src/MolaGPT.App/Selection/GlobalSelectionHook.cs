using System.ComponentModel;
using System.Runtime.InteropServices;
using static MolaGPT.App.Selection.SelectionNative;

namespace MolaGPT.App.Selection;

internal sealed class GlobalSelectionHook : IDisposable
{
    private readonly Thread _thread;
    private readonly HookProc _mouseCallback, _keyCallback;
    private readonly WinEventProc _foregroundCallback;
    private readonly Action<Point, nint, bool> _capture;
    private readonly Action _selectionStarted;
    private readonly Action _dismiss;
    private readonly Func<nint, bool> _isGameForeground;
    private readonly Action<bool> _gameStateChanged;
    private readonly Action<string?> _status;
    private readonly bool _hotkeyOnly;
    private readonly uint _modifiers, _key;
    private uint _threadId;
    private volatile bool _stopping;
    private volatile bool _toolbarVisible;
    private Point _down, _lastUp;
    private long _lastUpTime;
    private uint _captureTime;
    private bool _pressed, _dragged;
    private int _copyEpoch;
    private nint _mouseHook, _keyboardHook;
    private bool _hotkeyRegistered, _inputActive, _gameForeground;
    public int CopyEpoch => Volatile.Read(ref _copyEpoch);

    public GlobalSelectionHook(bool hotkeyOnly, uint modifiers, uint key,
        Action<Point, nint, bool> capture, Action selectionStarted, Action dismiss,
        Func<nint, bool> isGameForeground, Action<bool> gameStateChanged, Action<string?> status)
    {
        _hotkeyOnly = hotkeyOnly; _modifiers = modifiers; _key = key;
        _capture = capture; _selectionStarted = selectionStarted; _dismiss = dismiss;
        _isGameForeground = isGameForeground; _gameStateChanged = gameStateChanged; _status = status;
        _mouseCallback = Mouse; _keyCallback = Keyboard;
        _foregroundCallback = (_, _, hwnd, _, _, _, time) =>
        {
            // Out-of-context WinEvents may arrive after a newer mouse-up/hotkey.
            if (hwnd != GetForegroundWindow()) return;
            if (unchecked((int)(time - _captureTime)) > 0) _dismiss();
            RefreshForeground();
        };
        _thread = new Thread(Run) { IsBackground = true, Name = "Selection input" };
        _thread.Start();
    }

    private void Run()
    {
        nint foreground = 0;
        try
        {
            PeekMessage(out _, 0, 0, 0, 0); // Create this thread's message queue before exposing its id.
            _threadId = GetCurrentThreadId();
            if (_stopping) return;
            foreground = SetWinEventHook(3, 3, 0, _foregroundCallback, 0, 0, 0);
            if (foreground == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            UpdateForeground();
            while (!_stopping && GetMessage(out var msg, 0, 0, 0) > 0)
            {
                if (msg.Id == 0x0312)
                {
                    GetCursorPos(out var pt);
                    _captureTime = msg.Time;
                    _capture(pt, GetForegroundWindow(), true);
                }
                else if (msg.Id == 0x8001 && _hotkeyOnly)
                {
                    if (_inputActive && _toolbarVisible && _mouseHook == 0)
                    {
                        _mouseHook = SetWindowsHookEx(14, _mouseCallback, GetModuleHandle(null), 0);
                        if (_mouseHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    else if (!_toolbarVisible && _mouseHook != 0) { UnhookWindowsHookEx(_mouseHook); _mouseHook = 0; }
                }
                else if (msg.Id == 0x8002) UpdateForeground();
                else { TranslateMessage(ref msg); DispatchMessage(ref msg); }
            }
        }
        catch (Win32Exception ex) { _status($"无法启用划词：{ex.Message}"); }
        finally
        {
            StopInput();
            if (foreground != 0) UnhookWinEvent(foreground);
        }
    }

    private void UpdateForeground()
    {
        var game = _isGameForeground(GetForegroundWindow());
        if (_gameForeground != game)
        {
            _gameForeground = game;
            _gameStateChanged(game);
        }
        if (game)
        {
            if (_inputActive) { _dismiss(); StopInput(); }
            return;
        }
        if (_inputActive) return;
        _hotkeyRegistered = RegisterHotKey(0, 1, _modifiers | 0x4000, _key);
        _status(_hotkeyRegistered ? null : "快捷键被占用，请更换组合键。");
        if (!_hotkeyOnly || _toolbarVisible)
        {
            _mouseHook = SetWindowsHookEx(14, _mouseCallback, GetModuleHandle(null), 0);
            if (_mouseHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        _keyboardHook = SetWindowsHookEx(13, _keyCallback, GetModuleHandle(null), 0);
        if (_keyboardHook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _inputActive = true;
    }

    private void StopInput()
    {
        if (_mouseHook != 0) { UnhookWindowsHookEx(_mouseHook); _mouseHook = 0; }
        if (_keyboardHook != 0) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = 0; }
        if (_hotkeyRegistered) { UnregisterHotKey(0, 1); _hotkeyRegistered = false; }
        _inputActive = false;
        _pressed = false;
        _dragged = false;
        _lastUpTime = 0;
    }

    public void RefreshForeground()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, 0x8002, 0, 0);
    }

    private nint Mouse(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(0, code, message, data);
        var id = (uint)message;
        // No marshaling, allocation, UI dispatch, or UIA on ordinary pointer motion.
        if (id == 0x0200 && !_pressed) return CallNextHookEx(0, code, message, data);
        var m = Marshal.PtrToStructure<MouseData>(data);
        if ((m.Flags & 1) != 0) return CallNextHookEx(0, code, message, data);
        if (id == 0x0201)
        {
            var hwnd = GetAncestor(WindowFromPoint(m.Pt), 2);
            if (!IsOwn(hwnd))
            {
                if (_hotkeyOnly) _dismiss();
                else _selectionStarted();
            }
            _pressed = !IsOwn(hwnd); _dragged = false; _down = m.Pt;
            if (_hotkeyOnly) _pressed = false;
        }
        else if (id == 0x0200 && _pressed)
            _dragged |= Math.Abs(m.Pt.X - _down.X) >= 8 || Math.Abs(m.Pt.Y - _down.Y) >= 8;
        else if (id == 0x0202 && _pressed)
        {
            _pressed = false;
            var now = Environment.TickCount64;
            var doubleClick = now - _lastUpTime <= GetDoubleClickTime()
                && Math.Abs(m.Pt.X - _lastUp.X) <= 5 && Math.Abs(m.Pt.Y - _lastUp.Y) <= 5;
            // Retain consecutive clicks so a third click can capture a paragraph selection.
            _lastUp = m.Pt; _lastUpTime = _dragged ? 0 : now;
            var hwnd = GetForegroundWindow();
            if ((_dragged || doubleClick) && GetAncestor(WindowFromPoint(m.Pt), 2) == hwnd)
            {
                _captureTime = m.Time;
                _capture(m.Pt, hwnd, false);
            }
            else _dismiss();
        }
        else if (id is 0x0204 or 0x020A or 0x020E) _dismiss();
        return CallNextHookEx(0, code, message, data);
    }

    private nint Keyboard(int code, nint message, nint data)
    {
        if (code >= 0 && (uint)message is 0x0100 or 0x0104)
        {
            var key = Marshal.PtrToStructure<KeyData>(data);
            if ((key.Flags & 0x10) == 0 && (key.Key is 0x43 or 0x58 or 0x2D && GetAsyncKeyState(0x11) < 0
                || key.Key == 0x2E && GetAsyncKeyState(0x10) < 0)) Interlocked.Increment(ref _copyEpoch);
            if ((key.Flags & 0x10) == 0 && !IsOwn(GetForegroundWindow())) _dismiss();
        }
        return CallNextHookEx(0, code, message, data);
    }

    public void Dispose()
    {
        _stopping = true;
        if (_threadId != 0) PostThreadMessage(_threadId, 0x0012, 0, 0);
        if (Thread.CurrentThread != _thread) _thread.Join();
    }

    public void SetToolbarVisible(bool visible)
    {
        _toolbarVisible = visible;
        if (_hotkeyOnly && _threadId != 0) PostThreadMessage(_threadId, 0x8001, 0, 0);
    }
}
