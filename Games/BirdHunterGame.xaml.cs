using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace UnityAgent.Games
{
    public partial class BirdHunterGame : UserControl, IMinigame
    {
        public string GameName => "Bird Hunter";
        public string GameIcon => "\U0001F426"; // 🐦
        public string GameDescription => "Shoot as many birds as you can!";
        public UserControl View => this;
        public event Action? QuitRequested;

        private static readonly string HighScoreFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnityAgent", "birdhunter_highscore.txt");

        private const int GameDurationSeconds = 10;
        private static readonly string[] BirdChars = { "\U0001F426", "\U0001F985", "\U0001F986" }; // 🐦🦅🦆

        private readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly DispatcherTimer _spawnTimer = new();
        private readonly DispatcherTimer _countdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly Random _rng = new();
        private readonly List<BirdData> _birds = new();

        private int _score;
        private int _highScore;
        private int _timeRemaining;

        private class BirdData
        {
            public required TextBlock Element { get; init; }
            public required double Speed { get; init; }
            public required bool MovingRight { get; init; }
        }

        public BirdHunterGame()
        {
            InitializeComponent();
            _gameTimer.Tick += GameTimer_Tick;
            _spawnTimer.Tick += SpawnTimer_Tick;
            _countdownTimer.Tick += CountdownTimer_Tick;
            LoadHighScore();
        }

        public void Start() => ShowMenu();

        public void Stop()
        {
            _gameTimer.Stop();
            _spawnTimer.Stop();
            _countdownTimer.Stop();
            ClearBirds();
        }

        private void ShowMenu()
        {
            MenuPanel.Visibility = Visibility.Visible;
            GamePanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Collapsed;
            HighScoreMenuText.Text = _highScore > 0 ? $"High Score: {_highScore}" : "";
        }

        private void StartGame()
        {
            _score = 0;
            _timeRemaining = GameDurationSeconds;
            ClearBirds();

            MenuPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Collapsed;
            GamePanel.Visibility = Visibility.Visible;

            ScoreText.Text = "0";
            TimerText.Text = _timeRemaining.ToString();

            _spawnTimer.Interval = TimeSpan.FromMilliseconds(400);
            _gameTimer.Start();
            _spawnTimer.Start();
            _countdownTimer.Start();

            // Spawn first bird immediately
            SpawnBird();
        }

        private void GameTimer_Tick(object? sender, EventArgs e)
        {
            for (int i = _birds.Count - 1; i >= 0; i--)
            {
                var bird = _birds[i];
                double left = Canvas.GetLeft(bird.Element);
                double newLeft = bird.MovingRight ? left + bird.Speed : left - bird.Speed;
                Canvas.SetLeft(bird.Element, newLeft);

                // Remove bird if it flew off screen
                if ((bird.MovingRight && newLeft > GameCanvas.ActualWidth + 50) ||
                    (!bird.MovingRight && newLeft < -50))
                {
                    GameCanvas.Children.Remove(bird.Element);
                    _birds.RemoveAt(i);
                }
            }
        }

        private void SpawnTimer_Tick(object? sender, EventArgs e)
        {
            SpawnBird();

            // Gradually increase spawn rate as time passes
            double elapsed = GameDurationSeconds - _timeRemaining;
            double interval = Math.Max(200, 400 - elapsed * 15);
            _spawnTimer.Interval = TimeSpan.FromMilliseconds(interval);
        }

        private void SpawnBird()
        {
            if (GameCanvas.ActualHeight <= 0 || GameCanvas.ActualWidth <= 0) return;

            bool movingRight = _rng.Next(2) == 0;
            double speed = 2 + _rng.NextDouble() * 4; // 2–6 pixels per frame
            string birdChar = BirdChars[_rng.Next(BirdChars.Length)];
            int fontSize = 24 + _rng.Next(16); // 24–40

            var textBlock = new TextBlock
            {
                Text = birdChar,
                FontSize = fontSize,
                Cursor = Cursors.Hand,
                IsHitTestVisible = true
            };

            double maxY = Math.Max(0, GameCanvas.ActualHeight - fontSize - 10);
            double y = _rng.NextDouble() * maxY;
            double x = movingRight ? -40 : GameCanvas.ActualWidth + 10;

            Canvas.SetLeft(textBlock, x);
            Canvas.SetTop(textBlock, y);

            textBlock.MouseLeftButtonDown += Bird_Click;

            GameCanvas.Children.Add(textBlock);
            _birds.Add(new BirdData { Element = textBlock, Speed = speed, MovingRight = movingRight });
        }

        private void Bird_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBlock bird) return;

            var data = _birds.Find(b => b.Element == bird);
            if (data == null) return;

            _birds.Remove(data);
            GameCanvas.Children.Remove(bird);

            _score++;
            ScoreText.Text = _score.ToString();

            e.Handled = true;
        }

        private void CountdownTimer_Tick(object? sender, EventArgs e)
        {
            _timeRemaining--;
            TimerText.Text = _timeRemaining.ToString();

            if (_timeRemaining <= 0)
                EndGame();
        }

        private void EndGame()
        {
            _gameTimer.Stop();
            _spawnTimer.Stop();
            _countdownTimer.Stop();
            ClearBirds();

            GamePanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;

            FinalScoreText.Text = _score.ToString();

            if (_score > _highScore)
            {
                _highScore = _score;
                SaveHighScore();
                NewHighScoreText.Visibility = Visibility.Visible;
                HighScoreResultText.Text = "";
            }
            else
            {
                NewHighScoreText.Visibility = Visibility.Collapsed;
                HighScoreResultText.Text = _highScore > 0 ? $"High Score: {_highScore}" : "";
            }
        }

        private void ClearBirds()
        {
            _birds.Clear();
            GameCanvas.Children.Clear();
        }

        private void LoadHighScore()
        {
            try
            {
                if (File.Exists(HighScoreFile) &&
                    int.TryParse(File.ReadAllText(HighScoreFile).Trim(), out int saved) && saved > 0)
                    _highScore = saved;
            }
            catch { /* ignore corrupt or inaccessible file */ }
        }

        private void SaveHighScore()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HighScoreFile)!);
                File.WriteAllText(HighScoreFile, _highScore.ToString());
            }
            catch { /* ignore write failures */ }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { QuitRequested?.Invoke(); e.Handled = true; }
        }

        private void Start_Click(object sender, RoutedEventArgs e) => StartGame();
        private void TryAgain_Click(object sender, RoutedEventArgs e) => StartGame();
        private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();
    }
}
