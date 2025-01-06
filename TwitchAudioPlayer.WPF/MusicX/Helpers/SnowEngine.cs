using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TwitchAudioPlayer.WPF.MusicX.Models;

namespace TwitchAudioPlayer.WPF.MusicX.Helpers;

public class SnowEngine
{
    //Canvas to draw flakes in
    private readonly Canvas canvas;
    private readonly List<string> flakeImages = new();
    private readonly List<SnowInfo> flakes = new();
    private readonly int maxHorizontalSpeed = 3;
    private readonly int maxStartingSpeed = 10;
    private readonly int minHorizontalSpeed = 1;

    private readonly int minRadius = 5;

    private readonly int minStartingSpeed = 3;

    //Maximum flakes. It's calculating at the beginning and after canvas resize and depends on SnowCoverage property
    private int maxFlakes;

    private ushort snowCoverage = 5;

    /// <summary>
    ///     Constructor of SnowEngine
    /// </summary>
    /// <param name="canvas">canvas where snow falls</param>
    public SnowEngine(Canvas canvas, params string[] flakeImages)
    {
        if (canvas == null) throw new ArgumentNullException("canvas", "Canvas can't be null");
        if (flakeImages == null || flakeImages.Length == 0)
            throw new ArgumentException("Flakes images can't be empty", "flakeImages");

        this.canvas = canvas;
        canvas.IsHitTestVisible = false;
        canvas.SizeChanged += canvas_SizeChanged;
        this.flakeImages.AddRange(flakeImages);
    }

    /// <summary>
    ///     Minimum radius of flake
    /// </summary>
    public int MinRadius
    {
        get => minRadius;
        set => MaxRadius = value;
    }

    /// <summary>
    ///     Maximum radius of flake
    /// </summary>
    public int MaxRadius { get; set; } = 30;

    /// <summary>
    ///     Average snow coverage in percent
    /// </summary>
    public ushort SnowCoverage
    {
        get => snowCoverage;
        set
        {
            if (value > 100 || value < 1)
                throw new ArgumentOutOfRangeException("value", "Maximum coverage 100 and minumum 1");
            snowCoverage = value;
        }
    }

    /// <summary>
    ///     Vertical speed ratio of snow
    /// </summary>
    public double VerticalSpeedRatio { get; set; } = 0.1;

    /// <summary>
    ///     Horizontal speed ratio of snow
    /// </summary>
    public double HorizontalSpeedRatio { get; set; } = 0.08;

    /// <summary>
    ///     True if snow is falling, false if not
    /// </summary>
    public bool IsWorking { get; private set; }

    private void canvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RecalcMaxFlakes();
        SetFlakes(true);
    }

    /// <summary>
    ///     Start snowfall
    /// </summary>
    public void Start()
    {
        IsWorking = true;
        RecalcMaxFlakes();
        SetFlakes(true);
        CompositionTarget.Rendering += CompositionTarget_Rendering;
    }

    /// <summary>
    ///     Stop snowfall
    /// </summary>
    public void Stop()
    {
        IsWorking = false;
        CompositionTarget.Rendering -= CompositionTarget_Rendering;
        ClearSnow();
    }

    /// <summary>
    ///     Calculation of flakes number when canvas size changes
    /// </summary>
    private void RecalcMaxFlakes()
    {
        //Approximate maximum flakes in canvas
        var flakesInCanvas = canvas.ActualHeight * canvas.ActualWidth / (MaxRadius * MaxRadius);

        maxFlakes = (int)(flakesInCanvas * SnowCoverage / 100);
    }

    /// <summary>
    ///     Create image from resource of file
    /// </summary>
    /// <param name="path">path to image resource</param>
    /// <returns>Wpf-ready image</returns>
    private static BitmapImage CreateImage(string path)
    {
        var imgTemp = new BitmapImage();

        if (string.IsNullOrEmpty(path)) throw new ArgumentException("Path can't be empty", "path");

        try
        {
            if (!path.StartsWith("pack://") && !File.Exists(path)) return null;
        }
        catch
        {
        }

        imgTemp.BeginInit();
        imgTemp.CacheOption = BitmapCacheOption.OnLoad;
        imgTemp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        imgTemp.UriSource = new Uri(path);
        imgTemp.EndInit();
        if (imgTemp.CanFreeze) imgTemp.Freeze();

        return imgTemp;
    }

    /// <summary>
    ///     Adds missing flakes on canvas
    /// </summary>
    /// <param name="top">true if flakes appear on top of canvas, false if random Y position</param>
    private void SetFlakes(bool top = false)
    {
        var halfCanvasWidth = (int)canvas.ActualWidth / 2;
        var rand = new Random();
        Image flake = null;
        SnowInfo info = null;

        for (var i = flakes.Count; i < maxFlakes; i++)
        {
            //Flake creation
            flake = new Image();
            //Randomly selecting image
            flake.Source = CreateImage(flakeImages[rand.Next(0, flakeImages.Count)]);
            flake.Stretch = Stretch.Uniform;

            info = new SnowInfo(flake, VerticalSpeedRatio * rand.Next(minStartingSpeed, maxStartingSpeed),
                rand.Next(minRadius, MaxRadius));


            // Placing image  
            Canvas.SetLeft(flake, halfCanvasWidth + rand.Next(-halfCanvasWidth, halfCanvasWidth));
            if (!top)
                Canvas.SetTop(flake, rand.Next(0, (int)canvas.ActualHeight));
            else
                Canvas.SetTop(flake, -info.Radius * 2);
            canvas.Children.Add(flake);

            info.VelocityX = rand.Next(minHorizontalSpeed, maxHorizontalSpeed);
            flakes.Add(info);
        }
    }

    /// <summary>
    ///     Clears snow when stop
    /// </summary>
    private void ClearSnow()
    {
        for (var i = flakes.Count - 1; i >= 0; i--)
        {
            canvas.Children.Remove(flakes[i].Flake);
            flakes[i].Flake = null;
            flakes.RemoveAt(i);
        }
    }

    /// <summary>
    ///     Render snow
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void CompositionTarget_Rendering(object sender, EventArgs e)
    {
        var random = new Random();
        SnowInfo info = null;
        double left = 0;
        double top = 0;

        if (!IsWorking) return;

        //Add missing flakes
        if (flakes.Count < maxFlakes)
        {
            SetFlakes(true);
            return;
        }

        //Setting position of all flakes
        for (var i = flakes.Count - 1; i >= 0; i--)
        {
            info = flakes[i];
            left = Canvas.GetLeft(info.Flake);
            top = Canvas.GetTop(info.Flake);

            //.5 is magic number. Don't use magic numbers! :)
            flakes[i].VelocityX += .5 * HorizontalSpeedRatio;

            Canvas.SetLeft(flakes[i].Flake, left + Math.Cos(flakes[i].VelocityX));
            Canvas.SetTop(info.Flake, top + 1 * info.VelocityY);

            //Remove image from canvas when it leaves canvas
            if (top >= canvas.ActualHeight + info.Radius * 2)
            {
                flakes.Remove(info);
                canvas.Children.Remove(info.Flake);
            }
        }
    }
}