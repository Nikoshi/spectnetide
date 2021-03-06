﻿namespace Spect.Net.VsPackage.ToolWindows.Memory
{
    /// <summary>
    /// This enum represents the types of memory commands
    /// </summary>
    public enum MemoryCommandType
    {
        None,
        Invalid,
        Goto,
        SetRomPage,
        SetRamBank,
        MemoryMode
    }
}