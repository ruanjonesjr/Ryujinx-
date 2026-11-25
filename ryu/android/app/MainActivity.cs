using global::Android.App;
using global::Android.Content;
using global::Android.OS;
using global::Android.Views;
using global::Android.Widget;
using global::Android.Runtime;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.HLE.HOS.Services.Hid;
using RyujinxSwitch = Ryujinx.HLE.Switch;
using Ryujinx.Audio.Backends.CompatLayer;
using Ryujinx.Graphics.GAL;
using LibHac.Tools.FsSystem;
using Silk.NET.Vulkan;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Ryujinx.Android
{
    [Activity(Label = "Ryujinx Android", MainLauncher = true, Exported = true,
        ConfigurationChanges = global::Android.Content.PM.ConfigChanges.Orientation |
                              global::Android.Content.PM.ConfigChanges.ScreenSize |
                              global::Android.Content.PM.ConfigChanges.KeyboardHidden,
        ScreenOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape)]
    public sealed class MainActivity : Activity, ISurfaceHolderCallback
    {
        private const int PickGameRequest = 10;
        private const int PickKeysRequest = 11;
        private const int PickFirmwareRequest = 12;

        private SurfaceView _surfaceView;
        private TextView _status;
        private AndroidHost _host;
        private string _pendingAction;
        private bool _surfaceReady;

        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            Window.AddFlags(WindowManagerFlags.KeepScreenOn);

            var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
            var controls = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var gameButton = new Button(this) { Text = "ABRIR JOGO" };
            var keysButton = new Button(this) { Text = "IMPORTAR PROD.KEYS" };
            var firmwareButton = new Button(this) { Text = "INSTALAR FIRMWARE" };
            _status = new TextView(this) { Text = "Inicializando Ryujinx Android...", Gravity = GravityFlags.CenterVertical };
            gameButton.Click += (_, _) => PickFile(PickGameRequest, "*/*", "game");
            keysButton.Click += (_, _) => PickFile(PickKeysRequest, "*/*", "keys");
            firmwareButton.Click += (_, _) => PickFile(PickFirmwareRequest, "application/zip", "firmware");
            controls.AddView(gameButton, new LinearLayout.LayoutParams(0, -2, 1));
            controls.AddView(keysButton, new LinearLayout.LayoutParams(0, -2, 1));
            controls.AddView(firmwareButton, new LinearLayout.LayoutParams(0, -2, 1));
            root.AddView(controls);
            _surfaceView = new SurfaceView(this);
            _surfaceView.Holder.AddCallback(this);
            root.AddView(_surfaceView, new LinearLayout.LayoutParams(-1, 0, 1));
            var pad = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            AddPadButton(pad, "↑", ControllerKeys.DpadUp);
            AddPadButton(pad, "←", ControllerKeys.DpadLeft);
            AddPadButton(pad, "↓", ControllerKeys.DpadDown);
            AddPadButton(pad, "→", ControllerKeys.DpadRight);
            AddPadButton(pad, "A", ControllerKeys.A);
            AddPadButton(pad, "B", ControllerKeys.B);
            AddPadButton(pad, "X", ControllerKeys.X);
            AddPadButton(pad, "Y", ControllerKeys.Y);
            AddPadButton(pad, "L", ControllerKeys.L);
            AddPadButton(pad, "R", ControllerKeys.R);
            AddPadButton(pad, "ZL", ControllerKeys.Zl);
            AddPadButton(pad, "ZR", ControllerKeys.Zr);
            root.AddView(pad, new LinearLayout.LayoutParams(-1, -2));
            var sticks = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            AddStickControl(sticks, "ANALÓGICO ESQUERDO", true);
            AddStickControl(sticks, "ANALÓGICO DIREITO", false);
            root.AddView(sticks, new LinearLayout.LayoutParams(-1, 150));
            root.AddView(_status);
            SetContentView(root);
            Directory.CreateDirectory(Path.Combine(FilesDir.AbsolutePath, "Ryujinx"));
            _status.Text = "Aguardando superfície Vulkan...";
        }

        private void AddPadButton(LinearLayout row, string label, ControllerKeys key)
        {
            var button = new Button(this) { Text = label };
            button.SetPadding(2, 2, 2, 2);
            button.Touch += (_, e) =>
            {
                if (e.Event.Action == MotionEventActions.Down) _host?.SetButton(key, true);
                else if (e.Event.Action == MotionEventActions.Up || e.Event.Action == MotionEventActions.Cancel) _host?.SetButton(key, false);
                e.Handled = true;
            };
            row.AddView(button, new LinearLayout.LayoutParams(0, -2, 1));
        }

        private void AddStickControl(LinearLayout row, string label, bool left)
        {
            var stick = new TextView(this)
            {
                Text = label + "\\narraste para mover",
                Gravity = GravityFlags.Center,
                TextSize = 12
            };
            stick.SetBackgroundColor(unchecked((int)0xFF30343B));
            stick.SetTextColor(global::Android.Graphics.Color.White);
            stick.Touch += (_, e) =>
            {
                if (e.Event.Action == MotionEventActions.Down || e.Event.Action == MotionEventActions.Move)
                {
                    float nx = Math.Clamp((e.Event.GetX() / Math.Max(1, stick.Width)) * 2f - 1f, -1f, 1f);
                    float ny = Math.Clamp(1f - (e.Event.GetY() / Math.Max(1, stick.Height)) * 2f, -1f, 1f);
                    _host?.SetStick(left, (int)(nx * 32767), (int)(ny * 32767));
                }
                else if (e.Event.Action == MotionEventActions.Up || e.Event.Action == MotionEventActions.Cancel)
                {
                    _host?.SetStick(left, 0, 0);
                }
                e.Handled = true;
            };
            var lp = new LinearLayout.LayoutParams(0, -1, 1) { LeftMargin = 8, RightMargin = 8, TopMargin = 8, BottomMargin = 8 };
            row.AddView(stick, lp);
        }

        private void PickFile(int request, string mime, string action)
        {
            _pendingAction = action;
            var intent = new Intent(Intent.ActionOpenDocument);
            intent.AddCategory(Intent.CategoryOpenable);
            intent.SetType(mime);
            StartActivityForResult(intent, request);
        }

        protected override void OnActivityResult(int requestCode, global::Android.App.Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            if (resultCode != global::Android.App.Result.Ok || data?.Data == null) return;
            try
            {
                string name = QueryDisplayName(data.Data) ?? (_pendingAction == "keys" ? "prod.keys" : "selected-file");
                string targetDir = _pendingAction == "keys"
                    ? Path.Combine(FilesDir.AbsolutePath, "Ryujinx", "system")
                    : CacheDir.AbsolutePath;
                Directory.CreateDirectory(targetDir);
                string target = Path.Combine(targetDir, name);
                using (var input = ContentResolver.OpenInputStream(data.Data))
                using (var output = File.Create(target))
                    input.CopyTo(output);

                if (_pendingAction == "keys")
                {
                    _host?.ReloadKeys();
                    _status.Text = "Chaves importadas. Selecione um jogo.";
                }
                else if (_pendingAction == "firmware")
                {
                    _host?.InstallFirmware(target);
                    _status.Text = "Firmware instalado.";
                }
                else if (_pendingAction == "game")
                {
                    if (_host == null) _pendingGame = target;
                    else _ = StartGame(target);
                }
            }
            catch (Exception ex) { _status.Text = "Falha: " + ex.Message; }
        }

        private string _pendingGame;

        private string QueryDisplayName(global::Android.Net.Uri uri)
        {
            using var cursor = ContentResolver.Query(uri, null, null, null, null);
            if (cursor == null || !cursor.MoveToFirst()) return null;
            int index = cursor.GetColumnIndex(global::Android.Provider.OpenableColumns.DisplayName);
            return index >= 0 ? cursor.GetString(index) : null;
        }

        public void SurfaceCreated(ISurfaceHolder holder)
        {
            try
            {
                long nativeWindow = NativeFromSurface(JNIEnv.Handle, IntPtr.Zero, holder.Surface.Handle);
                if (nativeWindow == 0) throw new InvalidOperationException("ANativeWindow não foi criado.");
                _host = new AndroidHost(this, nativeWindow);
                _surfaceReady = true;
                _status.Text = "Vulkan inicializado. Importe prod.keys e selecione um jogo.";
                if (!string.IsNullOrEmpty(_pendingGame)) _ = StartGame(_pendingGame);
            }
            catch (Exception ex) { _status.Text = "Erro Vulkan: " + ex.Message; }
        }

        public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height)
        {
            _host?.Resize(width, height);
        }

        public void SurfaceDestroyed(ISurfaceHolder holder)
        {
            _surfaceReady = false;
            _host?.Stop();
            _host = null;
        }

        private async Task StartGame(string path)
        {
            try
            {
                _status.Text = "Carregando jogo...";
                bool loaded = await Task.Run(() => _host.LoadGame(path));
                _status.Text = loaded ? "Jogo carregado." : "O Ryujinx não reconheceu o jogo. Confira keys e firmware.";
                if (loaded) _host.StartRenderLoop();
            }
            catch (Exception ex) { _status.Text = "Falha ao carregar: " + ex.Message; }
        }

        protected override void OnDestroy()
        {
            _host?.Stop();
            base.OnDestroy();
        }

        [DllImport("ryujinx_android", EntryPoint = "Java_com_ryujinx_android_NativeWindowBridge_fromSurface")]
        private static extern long NativeFromSurface(IntPtr env, IntPtr clazz, IntPtr surface);

        [DllImport("ryujinx_android", EntryPoint = "Java_com_ryujinx_android_NativeWindowBridge_release")]
        private static extern void NativeReleaseWindow(IntPtr env, IntPtr clazz, long window);

        private sealed class AndroidHost
        {
            private readonly MainActivity _activity;
            private readonly long _nativeWindow;
            private readonly VirtualFileSystem _vfs;
            private readonly ContentManager _content;
            private readonly LibHacHorizonManager _libHac;
            private readonly VulkanRenderer _renderer;
            private RyujinxSwitch _device;
            private CancellationTokenSource _renderCancellation;
            private Task _renderTask;
            private ControllerKeys _buttons;
            private JoystickPosition _leftStick;
            private JoystickPosition _rightStick;

            public AndroidHost(MainActivity activity, long nativeWindow)
            {
                _activity = activity;
                _nativeWindow = nativeWindow;
                string data = Path.Combine(activity.FilesDir.AbsolutePath, "Ryujinx");
                Directory.CreateDirectory(data);
                AppDataManager.Initialize(data);

                _vfs = VirtualFileSystem.CreateInstance();
                _libHac = new LibHacHorizonManager();
                _libHac.InitializeFsServer(_vfs);
                _libHac.InitializeArpServer();
                _libHac.InitializeBcatServer();
                _libHac.InitializeSystemClients();
                _content = new ContentManager(_vfs);
                var accounts = new AccountManager(_libHac.RyujinxClient);
                var persistence = new UserChannelPersistence();

                _renderer = new VulkanRenderer(Vk.GetApi(), new IntPtr(nativeWindow), "");
                var configuration = new HLEConfiguration(
                    _vfs, _libHac, _content, accounts, persistence, _renderer,
                    new CompatLayerHardwareDeviceDriver(new AndroidAudioHardwareDeviceDriver()),
                    MemoryConfiguration.MemoryConfiguration4GiB,
                    new AndroidHostUIHandler(activity),
                    SystemLanguage.BrazilianPortuguese,
                    RegionCode.USA,
                    true, false, true, false,
                    IntegrityCheckLevel.None, 0, 0, "UTC",
                    MemoryManagerMode.HostMapped,
                    true, AspectRatio.Fixed16x9, 1.0f, false, "",
                    Ryujinx.Common.Configuration.Multiplayer.MultiplayerMode.Disabled);
                _device = new RyujinxSwitch(configuration);
                _renderer.Initialize(GraphicsDebugLevel.None);
            }

            public void ReloadKeys() => _vfs.ReloadKeySet();

            public void SetButton(ControllerKeys key, bool pressed)
            {
                if (pressed) _buttons |= key; else _buttons &= ~key;
                PushInput();
            }

            public void SetStick(bool left, int x, int y)
            {
                if (left) _leftStick = new JoystickPosition { Dx = x, Dy = y };
                else _rightStick = new JoystickPosition { Dx = x, Dy = y };
                PushInput();
            }

            private void PushInput()
            {
                _device?.Hid.Npads.Update(new System.Collections.Generic.List<GamepadInput>
                {
                    new GamepadInput
                    {
                        PlayerId = PlayerIndex.Handheld,
                        Buttons = _buttons,
                        LStick = _leftStick,
                        RStick = _rightStick
                    }
                });
            }

            public void InstallFirmware(string path) => _content.InstallFirmware(path);

            public bool LoadGame(string path)
            {
                _vfs.ReloadKeySet();
                string ext = Path.GetExtension(path).ToLowerInvariant();
                return ext == ".xci" ? _device.LoadXci(path) :
                       ext == ".nsp" ? _device.LoadNsp(path) :
                       ext == ".nro" ? _device.LoadProgram(path) : false;
            }

            public void Resize(int width, int height)
            {
                if (width > 0 && height > 0) _renderer.Window.SetSize(width, height);
            }

            public void StartRenderLoop()
            {
                if (_renderCancellation != null) return;
                _renderCancellation = new CancellationTokenSource();
                _renderTask = Task.Run(() => ((IRenderer)_renderer).RunLoop(() =>
                {
                    _device.Gpu.SetGpuThread();
                    _device.Gpu.InitializeShaderCache(_renderCancellation.Token);
                    while (!_renderCancellation.IsCancellationRequested)
                    {
                        if (_device.WaitFifo()) _device.ProcessFrame();
                        while (_device.ConsumeFrameAvailable())
                            _device.PresentFrame(() => { });
                    }
                }));
            }

            public void Stop()
            {
                _renderCancellation?.Cancel();
                _renderCancellation?.Dispose();
                _renderCancellation = null;
                try { _renderTask?.Wait(1500); } catch { }
                _device?.DisposeGpu();
                _device?.Dispose();
                NativeReleaseWindow(JNIEnv.Handle, IntPtr.Zero, _nativeWindow);
            }
        }
    }
}
