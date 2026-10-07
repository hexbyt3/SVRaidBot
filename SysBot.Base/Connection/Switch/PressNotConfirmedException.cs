using System;

namespace SysBot.Base
{
    /// <summary>
    /// A button press went out while the connection was failing, so it may never have
    /// reached the console. A menu macro that carries on after one ends up somewhere else.
    /// </summary>
    public sealed class PressNotConfirmedException(string message) : InvalidOperationException(message);
}
