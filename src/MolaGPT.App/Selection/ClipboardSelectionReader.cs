using System.Runtime.InteropServices;
using static MolaGPT.App.Selection.SelectionNative;

namespace MolaGPT.App.Selection;

internal static class ClipboardSelectionReader
{
    // Only explicit hotkeys reach here. Rich clipboard data is never reduced to plain text.
    internal static string? Read(nint source, nint owner, Func<bool> current, Func<int> copyEpoch)
    {
        if (!current() || owner == 0 || !OpenClipboard(owner)) return null;
        string? backup;
        uint sequence;
        try
        {
            for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
                if (format is not (1 or 7 or 13 or 16)) return null;
            backup = ReadText();
            sequence = GetClipboardSequenceNumber();
        }
        finally { CloseClipboard(); }
        if (!current()) return null;
        var epoch = copyEpoch();
        var inputs = new Input[]
        {
            new() { Type = 1, Key = 0x11 }, new() { Type = 1, Key = 0x2D },
            new() { Type = 1, Key = 0x2D, Flags = 2 }, new() { Type = 1, Key = 0x11, Flags = 2 }
        };
        var sent = SendInput(4, inputs, Marshal.SizeOf<Input>());
        if (sent != 4)
        {
            // A partial SendInput must not leave Ctrl or Insert pressed.
            if (sent > 0) SendInput(2, inputs[2..], Marshal.SizeOf<Input>());
            return null;
        }
        var end = Environment.TickCount64 + 180;
        while (GetClipboardSequenceNumber() == sequence && current() && copyEpoch() == epoch && Environment.TickCount64 < end)
            Thread.Sleep(10);
        if (GetClipboardSequenceNumber() == sequence || copyEpoch() != epoch || !OpenClipboard(owner)) return null;
        try
        {
            GetWindowThreadProcessId(GetClipboardOwner(), out var clipboardPid);
            GetWindowThreadProcessId(source, out var sourcePid);
            if (clipboardPid != sourcePid) return null;
            var text = ReadText();
            // The clipboard remains locked from read through restore, so a newer write cannot be overwritten.
            if (copyEpoch() == epoch)
            {
                if (!EmptyClipboard()) throw new InvalidOperationException("无法恢复剪贴板。");
                if (backup is not null) WriteText(backup);
            }
            return current() && text is { Length: > 0 and <= 16000 } ? text : null;
        }
        finally { CloseClipboard(); }
    }

    private static string? ReadText()
    {
        var handle = GetClipboardData(13);
        if (handle == 0) return null;
        var pointer = GlobalLock(handle);
        if (pointer == 0) throw new InvalidOperationException("无法读取剪贴板。");
        try { return Marshal.PtrToStringUni(pointer); }
        finally { GlobalUnlock(handle); }
    }

    private static void WriteText(string value)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(value + '\0');
        var mem = GlobalAlloc(2, (nuint)bytes.Length);
        if (mem == 0) throw new OutOfMemoryException();
        var ptr = GlobalLock(mem);
        if (ptr == 0) { GlobalFree(mem); throw new InvalidOperationException("无法恢复剪贴板。"); }
        try { Marshal.Copy(bytes, 0, ptr, bytes.Length); }
        finally { GlobalUnlock(mem); }
        if (SetClipboardData(13, mem) == 0) { GlobalFree(mem); throw new InvalidOperationException("无法恢复剪贴板。"); }
    }
}
