using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KeepAlive;

/// <summary>
/// Aplicație de tray care ține sesiunea "activă" (Teams, Slack etc.):
/// la fiecare interval (implicit 3 minute) verifică dacă utilizatorul nu a
/// atins tastatura/mouse-ul de cel puțin 2 minute și, dacă da, simulează o
/// apăsare a tastei F15 (o tastă pe care nicio aplicație uzuală nu o
/// folosește, deci nu produce efecte vizibile).
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // O singură instanță per sesiune de utilizator.
        using var mutex = new Mutex(initiallyOwned: true, name: @"Local\KeepAlive.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("KeepAlive rulează deja (vezi iconița din tray).", "KeepAlive",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var settings = Settings.Load(args);
        Log.Init(settings.LogPath);
        Log.Write($"pornit: interval {settings.Interval.TotalSeconds}s, prag idle {settings.IdleThreshold.TotalSeconds}s");

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext(settings));
    }
}

/// <summary>
/// Setările aplicației. Cele alese din meniu (interval, prevenire sleep) se
/// salvează în <c>%LOCALAPPDATA%\KeepAlive\settings.txt</c> (linii cheie=valoare).
/// Linia de comandă suprascrie fără a salva:
/// <c>KeepAlive.exe --interval 180 --idle 120 [--log C:\keepalive.log]</c> (valori în secunde).
/// </summary>
internal sealed record Settings(TimeSpan Interval, TimeSpan IdleThreshold, bool KeepAwake, string? LogPath)
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan DefaultIdleThreshold = TimeSpan.FromMinutes(2);
    public const bool DefaultKeepAwake = true;

    /// <summary>Opțiunile de interval oferite în meniu, în minute.</summary>
    public static readonly int[] IntervalChoicesMinutes = { 1, 2, 3, 5, 10 };

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeepAlive", "settings.txt");

    public static Settings Load(string[] args)
    {
        var saved = ReadStore();
        var interval = saved.TryGetValue("interval", out var im) && int.TryParse(im, out int minutes)
                       && Array.IndexOf(IntervalChoicesMinutes, minutes) >= 0
            ? TimeSpan.FromMinutes(minutes) : DefaultInterval;
        bool keepAwake = saved.TryGetValue("keepawake", out var ka) ? ka == "1" : DefaultKeepAwake;
        var idle = DefaultIdleThreshold;
        string? logPath = null;

        for (int i = 0; i + 1 < args.Length; i++)
        {
            string value = args[i + 1];
            switch (args[i])
            {
                case "--interval" when TryParseSeconds(value, out var t): interval = t; i++; break;
                case "--idle" when TryParseSeconds(value, out var t): idle = t; i++; break;
                case "--log": logPath = value; i++; break;
            }
        }

        return new Settings(interval, idle, keepAwake, logPath);
    }

    /// <summary>Persistă intervalul ales din meniu (doar dacă e una dintre opțiunile meniului).</summary>
    public static void SaveInterval(TimeSpan interval)
    {
        if (Array.IndexOf(IntervalChoicesMinutes, (int)interval.TotalMinutes) < 0
            || interval != TimeSpan.FromMinutes((int)interval.TotalMinutes))
            return; // valoare de test din linia de comandă, nu o persistăm
        SaveKey("interval", ((int)interval.TotalMinutes).ToString());
    }

    public static void SaveKeepAwake(bool keepAwake) => SaveKey("keepawake", keepAwake ? "1" : "0");

    /// <summary>Rescrie o singură cheie, păstrând restul fișierului. Eșecul la scriere nu e fatal.</summary>
    private static void SaveKey(string key, string value)
    {
        try
        {
            var map = ReadStore();
            map[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllLines(StorePath, map.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"nu am putut salva setările: {ex.Message}");
        }
    }

    private static Dictionary<string, string> ReadStore()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(StorePath)) return map;
            foreach (string line in File.ReadAllLines(StorePath))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return map;
    }

    private static bool TryParseSeconds(string s, out TimeSpan result)
    {
        bool ok = int.TryParse(s, out int seconds) && seconds > 0;
        result = ok ? TimeSpan.FromSeconds(seconds) : default;
        return ok;
    }
}

/// <summary>Log opțional pe fișier (activat cu <c>--log</c>); altfel scrie doar în Debug output.</summary>
internal static class Log
{
    private static string? _path;

    public static void Init(string? path) => _path = path;

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Debug.WriteLine(line);
        if (_path is null) return;
        try { File.AppendAllText(_path, line + Environment.NewLine); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* logul nu doboară aplicația */ }
    }
}

internal sealed class TrayAppContext : ApplicationContext
{
    private readonly Settings _settings;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _intervalMenu;
    private readonly ToolStripMenuItem _keepAwakeItem;
    private readonly Icon _icon;

    private TimeSpan _interval;
    private bool _keepAwake;
    private DateTime? _lastSimulated;
    private int _simulatedCount;

    public TrayAppContext(Settings settings)
    {
        _settings = settings;
        _interval = settings.Interval;
        _keepAwake = settings.KeepAwake;
        _icon = TrayIcons.Load("app.ico");

        _statusItem = new ToolStripMenuItem { Enabled = false };

        _keepAwakeItem = new ToolStripMenuItem("Previne sleep și stingerea ecranului")
        {
            CheckOnClick = true,
            Checked = _keepAwake,
        };
        _keepAwakeItem.CheckedChanged += (_, _) => SetKeepAwake(_keepAwakeItem.Checked);

        _intervalMenu = new ToolStripMenuItem("Interval");
        foreach (int minutes in Settings.IntervalChoicesMinutes)
        {
            var item = new ToolStripMenuItem($"{minutes} min") { Tag = minutes };
            item.Click += (s, _) => SetInterval(TimeSpan.FromMinutes((int)((ToolStripMenuItem)s!).Tag!));
            _intervalMenu.DropDownItems.Add(item);
        }

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_intervalMenu);
        menu.Items.Add(_keepAwakeItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Ieșire", null, (_, _) => Exit()));
        menu.Opening += (_, _) => RefreshStatus();

        _tray = new NotifyIcon
        {
            Icon = _icon,
            ContextMenuStrip = menu,
            Visible = true,
        };

        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += (_, _) => Simulate();
        ApplyInterval();
        ApplyKeepAwake();

        RefreshStatus();
    }

    private void SetInterval(TimeSpan interval)
    {
        if (interval == _interval) return;
        _interval = interval;
        Settings.SaveInterval(interval);
        Log.Write($"interval schimbat la {interval.TotalMinutes} min");
        ApplyInterval();
        RefreshStatus();
    }

    private void SetKeepAwake(bool enabled)
    {
        if (enabled == _keepAwake) return;
        _keepAwake = enabled;
        Settings.SaveKeepAwake(enabled);
        Log.Write(enabled ? "prevenire sleep: pornită" : "prevenire sleep: oprită");
        ApplyKeepAwake();
    }

    /// <summary>
    /// Îi spune Windows-ului că sistemul și ecranul sunt "în uz", deci nu intră în sleep
    /// și nu stinge ecranul cât timp aplicația rulează. Starea e per thread și persistă
    /// (ES_CONTINUOUS) până la următorul apel; o resetăm la oprire și la ieșire.
    /// </summary>
    private void ApplyKeepAwake()
    {
        uint flags = NativeMethods.ES_CONTINUOUS;
        if (_keepAwake) flags |= NativeMethods.ES_SYSTEM_REQUIRED | NativeMethods.ES_DISPLAY_REQUIRED;
        if (NativeMethods.SetThreadExecutionState(flags) == 0)
            Log.Write("SetThreadExecutionState a eșuat");
    }

    private void ApplyInterval()
    {
        _timer.Stop();
        _timer.Interval = (int)_interval.TotalMilliseconds;
        _timer.Start();
    }

    private void Simulate()
    {
        var idle = InputInfo.GetIdleTime();
        if (idle < _settings.IdleThreshold)
        {
            Log.Write($"idle {idle:mm\\:ss} < prag, nu simulez");
            return;
        }

        if (InputInfo.SendKeyPress(InputInfo.VK_F15))
        {
            _lastSimulated = DateTime.Now;
            _simulatedCount++;
            Log.Write($"F15 trimis (idle era {idle:mm\\:ss})");
        }
        else
        {
            Log.Write($"SendInput a eșuat: eroare Win32 {Marshal.GetLastWin32Error()}");
        }

        RefreshStatus();
    }

    private static string Human(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds:0} s" : $"{t.TotalMinutes:0.#} min";

    private void RefreshStatus()
    {
        string last = _lastSimulated is { } t ? $"Ultima simulare: {t:HH:mm:ss} (total {_simulatedCount})" : "Nicio simulare încă";
        _statusItem.Text = $"Activ · la {Human(_interval)}, dacă idle ≥ {Human(_settings.IdleThreshold)}\n{last}";

        foreach (ToolStripMenuItem item in _intervalMenu.DropDownItems)
            item.Checked = TimeSpan.FromMinutes((int)item.Tag!) == _interval;

        // Tooltip-ul de tray e limitat la 63 de caractere.
        string tip = $"KeepAlive – la {Human(_interval)}";
        if (_lastSimulated is { } lt) tip += $" · ultima {lt:HH:mm}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void Exit()
    {
        Log.Write("ieșire din meniu");
        _timer.Stop();
        NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>Iconița de tray, încărcată din resursele embedded (vezi Resources/ și tools/make_icons.py).</summary>
internal static class TrayIcons
{
    public static Icon Load(string resourceName)
    {
        using Stream? stream = typeof(TrayIcons).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            throw new InvalidOperationException($"Resursa embedded '{resourceName}' lipsește din exe.");

        // Dimensiunea de tray (16/20/24 px în funcție de DPI); Windows alege nivelul potrivit din .ico.
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}

/// <summary>Interop Win32: timp de inactivitate și simulare de tastă.</summary>
internal static class InputInfo
{
    public const ushort VK_F15 = 0x7E;

    /// <summary>Cât timp a trecut de la ultima apăsare de tastă / mișcare de mouse a utilizatorului.</summary>
    public static TimeSpan GetIdleTime()
    {
        var info = new NativeMethods.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<NativeMethods.LASTINPUTINFO>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
            return TimeSpan.Zero; // în caz de eroare, presupunem că e activ (nu simulăm degeaba)

        // Aritmetică pe uint: corectă și când tick count-ul dă peste cap (la ~49 zile).
        uint elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
        return TimeSpan.FromMilliseconds(elapsed);
    }

    /// <summary>Trimite key-down + key-up pentru tasta dată. Returnează false dacă SendInput a eșuat.</summary>
    public static bool SendKeyPress(ushort vk)
    {
        var inputs = new NativeMethods.INPUT[]
        {
            NativeMethods.INPUT.Key(vk, keyUp: false),
            NativeMethods.INPUT.Key(vk, keyUp: true),
        };
        uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        return sent == inputs.Length;
    }
}

internal static class NativeMethods
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public const uint ES_CONTINUOUS = 0x80000000;
    public const uint ES_SYSTEM_REQUIRED = 0x00000001;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint SetThreadExecutionState(uint esFlags);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    // Union-ul din INPUT trebuie să aibă dimensiunea celui mai mare membru (MOUSEINPUT).
    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;

        public static INPUT Key(ushort vk, bool keyUp) => new()
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = keyUp ? KEYEVENTF_KEYUP : 0 } },
        };
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
