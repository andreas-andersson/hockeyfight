// HockeyFightRenderer.cs
// Hockey Fight
//
// Port of Hockey_FightView.m (macOS ScreenSaverView) to GDI+.
//
// Coordinates: the macOS original draws in AppKit's bottom-left origin space.
// This port keeps every layout calculation in those same coordinates so it reads
// alongside the original, and flips to GDI+'s top-left origin in one place, the
// Rect() helper below. "y" throughout this file therefore means "distance from
// the bottom of the view", exactly as in Hockey_FightView.m.

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace HockeyFight;

internal sealed class HockeyFightRenderer : IDisposable
{
    // Sprite metrics. The sheets are a single row of fixed-size frames.
    private const float AudienceWidth = 40.0f;
    private const float AudienceHeight = 40.0f;
    private const float DigitWidth = 40.0f;
    private const float DigitHeight = 40.0f;
    private const float FlagWidth = 80.0f;
    private const float FlagHeight = 80.0f;

    private const int AudienceFrameCount = 3;   // audience.png is 120x40
    private const int FlagCount = 7;            // flags.png is 560x80

    // Background colour #f8f8f8.
    private static readonly Color IceColor = Color.FromArgb(0xf8, 0xf8, 0xf8);

    private readonly Bitmap? _zamboniSpriteSheet;   // Sprite sheet containing both zamboni frames
    private readonly Bitmap? _audienceSpriteSheet;  // Sprite sheet containing 3 audience frames
    private readonly Bitmap? _scoreboardImage;
    private readonly Bitmap? _netSpriteSheet;       // Sprite sheet containing left and right net frames
    private readonly Bitmap? _fontSpriteSheet;      // Sprite sheet containing digits 0-9 and ':', each 40x40
    private readonly Bitmap? _flagsSpriteSheet;     // Sprite sheet containing 7 flags, each 80x80

    private readonly Random _random = new();

    private int _currentZamboniFrame;               // 0 or 1
    private int[] _audienceTypes = [];              // Frame index (0-2) for each audience position (top row)
    private int[] _scoreboardAudienceTypes = [];    // Same, for the scoreboard row
    private int _audienceCount;
    private int _scoreboardAudienceCount;
    private int _selectedFlag1;                     // Index of first selected flag (0-6)
    private int _selectedFlag2;                     // Index of second selected flag (0-6)

    // Time integration variables
    private long _lastUpdateTicks;
    private double _accumulatedTime;                // For animation timing

    // Physics variables
    private float _xPosition;
    private float _yPosition;
    private float _velocityX = -60.0f;              // Pixels per second. Negative = moving left, ~2 pixels per frame at 30 FPS
    private float _velocityY = 0.0f;                // Pixels per second

    // Logical view size, in the same units the macOS original used.
    private float _width;
    private float _height;

    // The audience rows, nets, scoreboard, clock and flags only change once a
    // second, so they are composited into a cached bitmap and the moving zamboni
    // is drawn on top of it each frame.
    private Bitmap? _background;
    private bool _backgroundDirty = true;
    private string _backgroundClock = string.Empty;

    public HockeyFightRenderer()
    {
        _zamboniSpriteSheet = Sprites.Load("zamboni.png");
        _audienceSpriteSheet = Sprites.Load("audience.png");
        _scoreboardImage = Sprites.Load("scoreboard.png");
        _netSpriteSheet = Sprites.Load("net.png");
        _fontSpriteSheet = Sprites.Load("font.png");
        _flagsSpriteSheet = Sprites.Load("flags.png");
    }

    /// <summary>Size of a single zamboni frame; the sheet holds two side by side.</summary>
    private SizeF ZamboniFrameSize => _zamboniSpriteSheet is null
        ? new SizeF(100, 100)                       // matches the ObjC fallback of a 200x100 sheet
        : new SizeF(_zamboniSpriteSheet.Width / 2.0f, _zamboniSpriteSheet.Height);

    private SizeF ScoreboardSize => _scoreboardImage is null
        ? SizeF.Empty
        : new SizeF(_scoreboardImage.Width, _scoreboardImage.Height);

    /// <summary>
    /// Establishes the logical drawing size. Called whenever the host window is
    /// resized; the macOS original did this work in -initWithFrame:isPreview:
    /// because a ScreenSaverView is created at its final size.
    /// </summary>
    public void SetSize(float width, float height)
    {
        if (Math.Abs(width - _width) < 0.5f && Math.Abs(height - _height) < 0.5f)
        {
            return;
        }

        _width = width;
        _height = height;

        SetupAudience();
        ResetPosition();
        InvalidateBackground();
    }

    /// <summary>Port of -startAnimation.</summary>
    public void Start()
    {
        // Select two random unique flags (0-6)
        _selectedFlag1 = _random.Next(FlagCount);
        do
        {
            _selectedFlag2 = _random.Next(FlagCount);
        } while (_selectedFlag2 == _selectedFlag1);

        _lastUpdateTicks = 0;
        InvalidateBackground();
    }

    /// <summary>Port of -animateOneFrame.</summary>
    public void AnimateOneFrame()
    {
        if (_zamboniSpriteSheet is null)
        {
            return;
        }

        // Calculate delta time
        long currentTicks = Stopwatch.GetTimestamp();
        double deltaTime;

        if (_lastUpdateTicks == 0)
        {
            // First frame - use nominal frame time
            deltaTime = 1.0 / 30.0;
        }
        else
        {
            deltaTime = (currentTicks - _lastUpdateTicks) / (double)Stopwatch.Frequency;
            // Clamp delta time to prevent huge jumps
            deltaTime = Math.Min(deltaTime, 0.1);   // Max 100ms
        }
        _lastUpdateTicks = currentTicks;

        // Accumulate time for animation triggers
        _accumulatedTime += deltaTime;

        // Switch frames every 1 second
        if (_accumulatedTime >= 1.0)
        {
            _accumulatedTime -= 1.0;                // Keep remainder for smooth timing

            // Toggle between the two frames
            _currentZamboniFrame = _currentZamboniFrame == 0 ? 1 : 0;

            // Change 5 random audience members in top row
            for (int i = 0; i < 5 && i < _audienceCount; i++)
            {
                _audienceTypes[_random.Next(_audienceCount)] = _random.Next(AudienceFrameCount);
            }

            // Change 5 random audience members in scoreboard row
            for (int i = 0; i < 5 && i < _scoreboardAudienceCount; i++)
            {
                _scoreboardAudienceTypes[_random.Next(_scoreboardAudienceCount)] = _random.Next(AudienceFrameCount);
            }

            InvalidateBackground();
        }

        // Euler integration: position += velocity * deltaTime
        _xPosition += _velocityX * (float)deltaTime;
        _yPosition += _velocityY * (float)deltaTime;

        // Check if zamboni has moved off the left side of the screen
        if (_xPosition < -ZamboniFrameSize.Width)
        {
            // Reset to starting position with new random Y
            ResetPosition();
        }
    }

    /// <summary>Port of -drawRect:.</summary>
    public void Draw(Graphics g)
    {
        if (_width <= 0 || _height <= 0)
        {
            return;
        }

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingQuality = CompositingQuality.HighSpeed;
        g.SmoothingMode = SmoothingMode.None;

        DrawBackground(g);
        DrawZamboni(g);
    }

    /// <summary>
    /// Everything that changes at most once a second: the ice, both audience rows,
    /// the nets, the scoreboard, the clock and the flags.
    /// </summary>
    private void DrawBackground(Graphics g)
    {
        string clock = CurrentClock();

        if (_background is null ||
            _background.Width != (int)Math.Ceiling(_width) ||
            _background.Height != (int)Math.Ceiling(_height))
        {
            _background?.Dispose();
            _background = new Bitmap((int)Math.Ceiling(_width), (int)Math.Ceiling(_height));
            _backgroundDirty = true;
        }

        if (_backgroundDirty || clock != _backgroundClock)
        {
            using (Graphics bg = Graphics.FromImage(_background))
            {
                bg.InterpolationMode = InterpolationMode.NearestNeighbor;
                bg.PixelOffsetMode = PixelOffsetMode.Half;
                bg.CompositingQuality = CompositingQuality.HighSpeed;
                bg.SmoothingMode = SmoothingMode.None;

                RenderBackground(bg, clock);
            }

            _backgroundDirty = false;
            _backgroundClock = clock;
        }

        g.DrawImage(_background, new RectangleF(0, 0, _width, _height),
            new RectangleF(0, 0, _background.Width, _background.Height), GraphicsUnit.Pixel);
    }

    private void RenderBackground(Graphics g, string clock)
    {
        // Fill background with light gray (#f8f8f8)
        using (var ice = new SolidBrush(IceColor))
        {
            g.FillRectangle(ice, 0, 0, _width, _height);
        }

        DrawTopAudienceRow(g);
        DrawScoreboardAudienceRows(g);
        DrawScoreboardRow(g, clock);
    }

    private void DrawTopAudienceRow(Graphics g)
    {
        if (_audienceSpriteSheet is null || _audienceCount == 0)
        {
            return;
        }

        float audienceY = MathF.Floor(_height - AudienceHeight);

        for (int i = 0; i < _audienceCount; i++)
        {
            float audienceX = MathF.Floor(i * AudienceWidth);
            RectangleF destRect = Rect(audienceX, audienceY, AudienceWidth, AudienceHeight);

            // Select frame from sprite sheet: frame 0 is at x=0, frame 1 is at x=40, frame 2 is at x=80
            int frameIndex = _audienceTypes[i];
            var sourceRect = new RectangleF(frameIndex * AudienceWidth, 0, AudienceWidth, AudienceHeight);

            g.DrawImage(_audienceSpriteSheet, destRect, sourceRect, GraphicsUnit.Pixel);
        }
    }

    private void DrawScoreboardAudienceRows(Graphics g)
    {
        if (_audienceSpriteSheet is null || _scoreboardImage is null || _scoreboardAudienceCount == 0)
        {
            return;
        }

        SizeF scoreboardSize = ScoreboardSize;

        float startY = MathF.Floor(_height - AudienceHeight - scoreboardSize.Height);
        int rowsInScoreboard = (int)MathF.Ceiling(scoreboardSize.Height / AudienceHeight);

        int index = 0;
        for (int row = 0; row < rowsInScoreboard; row++)
        {
            float audienceY = MathF.Floor(startY + row * AudienceHeight);

            for (int col = 0; col < _audienceCount; col++)
            {
                if (index >= _scoreboardAudienceCount)
                {
                    break;
                }

                float audienceX = MathF.Floor(col * AudienceWidth);
                RectangleF destRect = Rect(audienceX, audienceY, AudienceWidth, AudienceHeight);

                int frameIndex = _scoreboardAudienceTypes[index];
                var sourceRect = new RectangleF(frameIndex * AudienceWidth, 0, AudienceWidth, AudienceHeight);

                g.DrawImage(_audienceSpriteSheet, destRect, sourceRect, GraphicsUnit.Pixel);
                index++;
            }
        }
    }

    /// <summary>Left net padding, centre scoreboard, right net padding, clock and flags.</summary>
    private void DrawScoreboardRow(Graphics g, string clock)
    {
        if (_scoreboardImage is null)
        {
            return;
        }

        SizeF scoreboardSize = ScoreboardSize;

        // Center horizontally (rounded to whole pixel)
        float scoreboardX = MathF.Floor((_width - scoreboardSize.Width) / 2.0f);
        // Position below audience (rounded to whole pixel)
        float scoreboardY = MathF.Floor(_height - AudienceHeight - scoreboardSize.Height);

        DrawNetPadding(g, scoreboardX, scoreboardY, scoreboardSize);

        // Draw center scoreboard
        RectangleF scoreboardRect = Rect(scoreboardX, scoreboardY, scoreboardSize.Width, scoreboardSize.Height);
        g.DrawImage(_scoreboardImage, scoreboardRect,
            new RectangleF(0, 0, _scoreboardImage.Width, _scoreboardImage.Height), GraphicsUnit.Pixel);

        DrawClock(g, scoreboardX, scoreboardY, scoreboardSize, clock);
        DrawFlags(g, scoreboardX, scoreboardY);
    }

    private void DrawNetPadding(Graphics g, float scoreboardX, float scoreboardY, SizeF scoreboardSize)
    {
        if (_netSpriteSheet is null)
        {
            return;
        }

        // Each frame is half the width of the sprite sheet
        float frameWidth = _netSpriteSheet.Width / 2.0f;
        float frameHeight = _netSpriteSheet.Height;

        // Draw left padding (frame 0) - work backwards from scoreboard edge
        var leftSourceRect = new RectangleF(0, 0, frameWidth, frameHeight);
        float currentX = MathF.Floor(scoreboardX - frameWidth);

        while (currentX >= 0)
        {
            RectangleF leftRect = Rect(MathF.Floor(currentX), scoreboardY, frameWidth, frameHeight);
            g.DrawImage(_netSpriteSheet, leftRect, leftSourceRect, GraphicsUnit.Pixel);
            currentX -= frameWidth;
        }

        // Handle partial tile on far left if needed
        if (currentX + frameWidth > 0)
        {
            float partialWidth = currentX + frameWidth;
            RectangleF partialDestRect = Rect(0, scoreboardY, partialWidth, frameHeight);
            var partialSourceRect = new RectangleF(frameWidth - partialWidth, 0, partialWidth, frameHeight);
            g.DrawImage(_netSpriteSheet, partialDestRect, partialSourceRect, GraphicsUnit.Pixel);
        }

        // Draw right padding (frame 1) - work forwards from scoreboard edge
        var rightSourceRect = new RectangleF(frameWidth, 0, frameWidth, frameHeight);
        currentX = MathF.Floor(scoreboardX + scoreboardSize.Width);

        while (currentX + frameWidth <= _width)
        {
            RectangleF rightRect = Rect(MathF.Floor(currentX), scoreboardY, frameWidth, frameHeight);
            g.DrawImage(_netSpriteSheet, rightRect, rightSourceRect, GraphicsUnit.Pixel);
            currentX += frameWidth;
        }

        // Handle partial tile on far right if needed
        if (currentX < _width)
        {
            float partialWidth = _width - currentX;
            RectangleF partialDestRect = Rect(currentX, scoreboardY, partialWidth, frameHeight);
            var partialSourceRect = new RectangleF(frameWidth, 0, partialWidth, frameHeight);
            g.DrawImage(_netSpriteSheet, partialDestRect, partialSourceRect, GraphicsUnit.Pixel);
        }
    }

    private void DrawClock(Graphics g, float scoreboardX, float scoreboardY, SizeF scoreboardSize, string clock)
    {
        // clock is "HHmmss"; the scoreboard shows HH:mm in the middle and the two
        // seconds digits in their own windows either side of it.
        string timeString = $"{clock[..2]}:{clock[2..4]}";
        string secondsString = clock[4..];

        // Draw current time centered on scoreboard
        float totalWidth = timeString.Length * DigitWidth;   // 5 characters (HH:MM)
        float timeX = MathF.Floor(scoreboardX + (scoreboardSize.Width - totalWidth) / 2.0f);
        float timeY = MathF.Floor(scoreboardY + (scoreboardSize.Height - DigitHeight) / 2.0f);
        DrawNumber(g, timeString, timeX, timeY);

        // Draw current seconds as two separate digits
        // First digit at x offset 336, vertically centered
        float firstDigitX = MathF.Floor(scoreboardX + 336);
        float firstDigitY = MathF.Floor(scoreboardY + (scoreboardSize.Height - DigitHeight) / 2.0f);
        DrawNumber(g, secondsString[..1], firstDigitX, firstDigitY);

        // Second digit at x offset 986, vertically centered
        float secondDigitX = MathF.Floor(scoreboardX + 986);
        float secondDigitY = MathF.Floor(scoreboardY + (scoreboardSize.Height - DigitHeight) / 2.0f);
        DrawNumber(g, secondsString[1..], secondDigitX, secondDigitY);
    }

    private void DrawFlags(Graphics g, float scoreboardX, float scoreboardY)
    {
        if (_flagsSpriteSheet is null)
        {
            return;
        }

        // Draw first flag at scoreboard position + (165, 25)
        var flag1SourceRect = new RectangleF(_selectedFlag1 * FlagWidth, 0, FlagWidth, FlagHeight);
        RectangleF flag1DestRect = Rect(MathF.Floor(scoreboardX + 165), MathF.Floor(scoreboardY + 25), FlagWidth, FlagHeight);
        g.DrawImage(_flagsSpriteSheet, flag1DestRect, flag1SourceRect, GraphicsUnit.Pixel);

        // Draw second flag at scoreboard position + (1047, 25)
        var flag2SourceRect = new RectangleF(_selectedFlag2 * FlagWidth, 0, FlagWidth, FlagHeight);
        RectangleF flag2DestRect = Rect(MathF.Floor(scoreboardX + 1047), MathF.Floor(scoreboardY + 25), FlagWidth, FlagHeight);
        g.DrawImage(_flagsSpriteSheet, flag2DestRect, flag2SourceRect, GraphicsUnit.Pixel);
    }

    private void DrawZamboni(Graphics g)
    {
        if (_zamboniSpriteSheet is null)
        {
            return;
        }

        SizeF frame = ZamboniFrameSize;

        // Validate frame size
        if (frame.Width <= 0 || frame.Height <= 0)
        {
            Debug.WriteLine($"Invalid zamboni frame size: {frame.Width:F0}x{frame.Height:F0}");
            return;
        }

        // Select frame from sprite sheet
        var sourceRect = new RectangleF(_currentZamboniFrame * frame.Width, 0, frame.Width, frame.Height);

        // Use current position (rounded to whole pixels)
        RectangleF destRect = Rect(MathF.Floor(_xPosition), MathF.Floor(_yPosition), frame.Width, frame.Height);

        g.DrawImage(_zamboniSpriteSheet, destRect, sourceRect, GraphicsUnit.Pixel);
    }

    /// <summary>Port of -drawNumber:atX:y:.</summary>
    private void DrawNumber(Graphics g, string numberString, float x, float y)
    {
        if (_fontSpriteSheet is null)
        {
            return;
        }

        float currentX = MathF.Floor(x);     // Round initial position
        float roundedY = MathF.Floor(y);     // Round Y position

        foreach (char character in numberString)
        {
            int spriteIndex = -1;

            // Convert character to sprite index
            if (character is >= '0' and <= '9')
            {
                spriteIndex = character - '0';   // 0-9
            }
            else if (character == ':')
            {
                spriteIndex = 10;                // Colon is at index 10
            }

            if (spriteIndex >= 0)
            {
                // Extract character from sprite sheet
                var sourceRect = new RectangleF(spriteIndex * DigitWidth, 0, DigitWidth, DigitHeight);
                RectangleF destRect = Rect(MathF.Floor(currentX), roundedY, DigitWidth, DigitHeight);

                g.DrawImage(_fontSpriteSheet, destRect, sourceRect, GraphicsUnit.Pixel);

                currentX += DigitWidth;
            }
        }
    }

    /// <summary>Port of -setupAudience.</summary>
    private void SetupAudience()
    {
        SizeF scoreboardSize = ScoreboardSize;

        // Calculate how many audience members fit across the screen (top row)
        _audienceCount = (int)MathF.Ceiling(_width / AudienceWidth);

        // Initialize array with random audience types (top row)
        _audienceTypes = new int[_audienceCount];
        for (int i = 0; i < _audienceCount; i++)
        {
            _audienceTypes[i] = _random.Next(AudienceFrameCount);   // 0, 1, or 2
        }

        // Calculate how many audience members fit in scoreboard row
        int rowsInScoreboard = (int)MathF.Ceiling(scoreboardSize.Height / AudienceHeight);
        _scoreboardAudienceCount = _audienceCount * rowsInScoreboard;

        // Initialize array with random audience types (scoreboard row)
        _scoreboardAudienceTypes = new int[_scoreboardAudienceCount];
        for (int i = 0; i < _scoreboardAudienceCount; i++)
        {
            _scoreboardAudienceTypes[i] = _random.Next(AudienceFrameCount);
        }
    }

    /// <summary>Port of -resetPosition.</summary>
    private void ResetPosition()
    {
        SizeF scoreboardSize = ScoreboardSize;
        SizeF zamboni = ZamboniFrameSize;

        // Start off-screen to the right
        _xPosition = _width;

        // Random Y position keeping image fully on screen and at least 100 pixels below scoreboard
        float minY = AudienceHeight + scoreboardSize.Height + 100.0f;
        float maxY = _height - zamboni.Height;
        _yPosition = maxY > minY ? minY + _random.Next((int)(maxY - minY)) : minY;
    }

    /// <summary>
    /// Converts a rectangle from the macOS bottom-left origin space used throughout
    /// this file into the top-left origin space GDI+ draws in. This is the only
    /// place the two coordinate systems meet.
    /// </summary>
    private RectangleF Rect(float x, float y, float width, float height) =>
        new(x, _height - y - height, width, height);

    private void InvalidateBackground() => _backgroundDirty = true;

    /// <summary>
    /// Current wall clock as "HHmmss". Invariant culture keeps the digits ASCII so
    /// they map onto the font sprite sheet regardless of the user's locale.
    /// </summary>
    private static string CurrentClock() =>
        DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _zamboniSpriteSheet?.Dispose();
        _audienceSpriteSheet?.Dispose();
        _scoreboardImage?.Dispose();
        _netSpriteSheet?.Dispose();
        _fontSpriteSheet?.Dispose();
        _flagsSpriteSheet?.Dispose();
        _background?.Dispose();
    }
}
