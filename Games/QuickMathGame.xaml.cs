using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace AgenticEngine.Games
{
    public partial class QuickMathGame : UserControl, IMinigame
    {
        public string GameName => "Quick Math";
        public string GameIcon => "\u2211"; // sigma symbol
        public string GameDescription => "Solve as many math equations as you can in 10 seconds";
        public UserControl View => this;
        public event Action? QuitRequested;

        private const double TotalSeconds = 10.0;

        private readonly DispatcherTimer _gameTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly Random _rng = new();
        private DateTime _startTime;
        private int _score;
        private int _correctAnswer;
        private int _highScore;

        public QuickMathGame()
        {
            InitializeComponent();
            _gameTimer.Tick += GameTimer_Tick;
        }

        public void Start()
        {
            ShowMenu();
        }

        public void Stop()
        {
            _gameTimer.Stop();
            _score = 0;
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
            _startTime = DateTime.UtcNow;

            MenuPanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Collapsed;
            GamePanel.Visibility = Visibility.Visible;

            ScoreText.Text = "Score: 0";
            FeedbackText.Text = "";

            GenerateEquation();
            AnswerBox.Text = "";
            AnswerBox.Focus();

            _gameTimer.Start();
        }

        private void GenerateEquation()
        {
            // Pick random operation: +, -, x
            int op = _rng.Next(3);
            int a, b;

            switch (op)
            {
                case 0: // addition
                    a = _rng.Next(1, 50);
                    b = _rng.Next(1, 50);
                    EquationText.Text = $"{a} + {b}";
                    _correctAnswer = a + b;
                    break;
                case 1: // subtraction (ensure non-negative result)
                    a = _rng.Next(1, 50);
                    b = _rng.Next(1, a + 1);
                    EquationText.Text = $"{a} - {b}";
                    _correctAnswer = a - b;
                    break;
                default: // multiplication
                    a = _rng.Next(2, 13);
                    b = _rng.Next(2, 13);
                    EquationText.Text = $"{a} \u00D7 {b}";
                    _correctAnswer = a * b;
                    break;
            }
        }

        private void GameTimer_Tick(object? sender, EventArgs e)
        {
            double elapsed = (DateTime.UtcNow - _startTime).TotalSeconds;
            double remaining = Math.Max(0, TotalSeconds - elapsed);

            TimerText.Text = $"{remaining:F1}s";

            // Update timer bar width
            TimerBar.Width = (remaining / TotalSeconds) * (TimerBar.Parent as Border)!.ActualWidth;

            if (remaining <= 0)
            {
                _gameTimer.Stop();
                ShowResult();
            }
        }

        private void CheckAnswer()
        {
            if (!int.TryParse(AnswerBox.Text.Trim(), out int answer)) return;

            if (answer == _correctAnswer)
            {
                _score++;
                ScoreText.Text = $"Score: {_score}";
                FeedbackText.Text = "";
                GenerateEquation();
                AnswerBox.Text = "";
            }
        }

        private void ShowResult()
        {
            GamePanel.Visibility = Visibility.Collapsed;
            ResultPanel.Visibility = Visibility.Visible;

            FinalScoreText.Text = _score.ToString();

            if (_score > _highScore)
            {
                _highScore = _score;
                NewHighScoreText.Visibility = Visibility.Visible;
                HighScoreResultText.Text = "";
            }
            else
            {
                NewHighScoreText.Visibility = Visibility.Collapsed;
                HighScoreResultText.Text = $"High Score: {_highScore}";
            }
        }

        private void AnswerBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CheckAnswer();
                e.Handled = true;
            }
        }

        private void AnswerBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Auto-submit: check answer on every keystroke for speed
            if (string.IsNullOrWhiteSpace(AnswerBox.Text)) return;
            if (!int.TryParse(AnswerBox.Text.Trim(), out int answer)) return;

            if (answer == _correctAnswer)
            {
                _score++;
                ScoreText.Text = $"Score: {_score}";
                FeedbackText.Text = "";
                GenerateEquation();
                AnswerBox.Text = "";
            }
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
