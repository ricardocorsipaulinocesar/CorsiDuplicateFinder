using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CorsiDuplicate.UI.Views;

public partial class VideoPlayerWindow : Window
{
    private bool _isPlaying = true;
    private bool _isDraggingScrubber;
    private readonly DispatcherTimer _positionTimer;

    public VideoPlayerWindow(string filePath, string title)
    {
        InitializeComponent();
        Title = title;
        Player.Source = new Uri(filePath);
        Player.Play();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => UpdatePositionDisplay();
        _positionTimer.Start();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan)
        {
            Scrubber.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
            DurationText.Text = FormatTime(Player.NaturalDuration.TimeSpan);
        }
    }

    private void UpdatePositionDisplay()
    {
        if (_isDraggingScrubber)
        {
            return;
        }

        Scrubber.Value = Player.Position.TotalSeconds;
        PositionText.Text = FormatTime(Player.Position);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void Scrubber_PreviewMouseDown(object sender, MouseButtonEventArgs e) => _isDraggingScrubber = true;

    private void Scrubber_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingScrubber = false;
        Player.Position = TimeSpan.FromSeconds(Scrubber.Value);
    }

    private void Scrubber_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isDraggingScrubber)
        {
            PositionText.Text = FormatTime(TimeSpan.FromSeconds(e.NewValue));
        }
    }

    private void SeekBack_Click(object sender, RoutedEventArgs e) => Seek(TimeSpan.FromSeconds(-10));

    private void SeekForward_Click(object sender, RoutedEventArgs e) => Seek(TimeSpan.FromSeconds(10));

    private void Seek(TimeSpan delta)
    {
        var target = Player.Position + delta;
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }
        Player.Position = target;
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            Player.Pause();
            PlayPauseButton.Content = "Play";
        }
        else
        {
            Player.Play();
            PlayPauseButton.Content = "Pause";
        }
        _isPlaying = !_isPlaying;
    }

    private void RestartButton_Click(object sender, RoutedEventArgs e)
    {
        Player.Position = TimeSpan.Zero;
        Player.Play();
        PlayPauseButton.Content = "Pause";
        _isPlaying = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _positionTimer.Stop();
        Player.Stop();
        Player.Close();
        base.OnClosed(e);
    }
}
