using System;

namespace SysBot.Base
{
    /// <summary>
    /// The console could not read the requested memory. sys-botbase 2.5+ answers an
    /// unreadable address or a broken pointer chain with an empty line.
    /// </summary>
    public sealed class SwitchReadFailedException(string message) : InvalidOperationException(message);
}
