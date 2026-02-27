using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace UnityAgent.Games
{
    public partial class ReactionTestGame : UserControl, IMinigame
    {
        public string GameName => "Reaction Test";
        public string GameIcon => "\u25CF"; // filled circle
        public string GameDescription => "Click the dot as fast as you can";
        public UserControl View => this;
        public event Action? QuitRequested;

        private static readonly string HighScoreFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnityAgent", "reaction_highscore.txt");

        private readonly DispatcherTimer _delayTimer = new();
        private readonly Stopwatch _stopwatch = new();
        private readonly Random _rng = new();
        private bool _dotVisible;
        private long _bestTime; // 0 means no best time yet

        public ReactionTestGame()
        {
            InitializeComponent();
            _delayTimer.Tick += DelayTimer_Tick;
            LoadHighScore();
        }

        public void Start()
        {
            ShowMenu();
        }

        public void Stop()
        {
            _delayTimer.Stop();
            _stopwatch.Reset();
            _dotVisible = false;
        }

        private void ShowMenu()
        {
            MenuPanel.Visibility = Visibility.Visible;
            GameCanvas.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Collapsed;
            BestTimeMenuText.Text = _bestTime > 0 ? $"Best Time: {_bestTime} ms" : "";
        }

        private void StartRound()
        {
            _dotVisible = false;
            _stopwatch.Reset();

            MenuPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Collapsed;
            GameCanvas.Visibility = Visibility.Visible;

            TargetDot.Visibility = Visibility.Collapsed;
            TooEarlyText.Visibility = Visibility.Collapsed;

            // Center the "wait" text
            GameCanvas.UpdateLayout();
            WaitText.Visibility = Visibility.Visible;
            Canvas.SetLeft(WaitText, (GameCanvas.ActualWidth - WaitText.ActualWidth) / 2);
            Canvas.SetTop(WaitText, (GameCanvas.ActualHeight - WaitText.ActualHeight) / 2);

            // Random delay between 1.5 and 5 seconds
            _delayTimer.Interval = TimeSpan.FromMilliseconds(1500 + _rng.Next(3500));
            _delayTimer.Start();
        }

        private void DelayTimer_Tick(object? sender, EventArgs e)
        {
            _delayTimer.Stop();
            WaitText.Visibility = Visibility.Collapsed;

            // Place dot at random position within canvas bounds
            GameCanvas.UpdateLayout();
            double maxX = Math.Max(0, GameCanvas.ActualWidth - TargetDot.Width);
            double maxY = Math.Max(0, GameCanvas.ActualHeight - TargetDot.Height);
            Canvas.SetLeft(TargetDot, _rng.NextDouble() * maxX);
            Canvas.SetTop(TargetDot, _rng.NextDouble() * maxY);

            TargetDot.Visibility = Visibility.Visible;
            _dotVisible = true;
            _stopwatch.Restart();
        }

        private void GameCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!_dotVisible)
            {
                // Clicked too early
                _delayTimer.Stop();
                WaitText.Visibility = Visibility.Collapsed;
                TooEarlyText.Visibility = Visibility.Visible;
                Canvas.SetLeft(TooEarlyText, 20);
                Canvas.SetTop(TooEarlyText, (GameCanvas.ActualHeight - 30) / 2);

                // Restart after a brief pause
                var restart = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                restart.Tick += (_, _) =>
                {
                    restart.Stop();
                    StartRound();
                };
                restart.Start();
                return;
            }

            // Check if click was on the dot
            var pos = e.GetPosition(TargetDot);
            bool hitDot = pos.X >= 0 && pos.X <= TargetDot.Width &&
                          pos.Y >= 0 && pos.Y <= TargetDot.Height;
            if (!hitDot) return;

            _stopwatch.Stop();
            _dotVisible = false;
            ShowResult(_stopwatch.ElapsedMilliseconds);
        }

        private void ShowResult(long ms)
        {
            GameCanvas.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;
            ResultTime.Text = ms.ToString();

            if (_bestTime == 0 || ms < _bestTime)
            {
                _bestTime = ms;
                SaveHighScore();
                NewBestText.Visibility = Visibility.Visible;
                BestTimeResultText.Text = "";
            }
            else
            {
                NewBestText.Visibility = Visibility.Collapsed;
                BestTimeResultText.Text = $"Best Time: {_bestTime} ms";
            }
        }

        private void LoadHighScore()
        {
            try
            {
                if (File.Exists(HighScoreFile) && long.TryParse(File.ReadAllText(HighScoreFile).Trim(), out long saved) && saved > 0)
                    _bestTime = saved;
            }
            catch { /* ignore corrupt or inaccessible file */ }
        }

        private void SaveHighScore()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HighScoreFile)!);
                File.WriteAllText(HighScoreFile, _bestTime.ToString());
            }
            catch { /* ignore write failures */ }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { QuitRequested?.Invoke(); e.Handled = true; }
        }

        private void Start_Click(object sender, RoutedEventArgs e) => StartRound();
        private void TryAgain_Click(object sender, RoutedEventArgs e) => StartRound();
        private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();
    }
}
