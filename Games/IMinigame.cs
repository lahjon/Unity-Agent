using System;
using System.Windows.Controls;

namespace UnityAgent.Games
{
    public interface IMinigame
    {
        string GameName { get; }
        string GameIcon { get; } // Unicode/emoji character for the icon
        string GameDescription { get; }
        UserControl View { get; }
        event Action? QuitRequested;
        void Start();
        void Stop();
    }
}
