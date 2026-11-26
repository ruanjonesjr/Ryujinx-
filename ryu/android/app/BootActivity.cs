using global::Android.App;
using global::Android.OS;
using global::Android.Content;

namespace Ryujinx.Android
{
    // Lightweight launcher: enter the emulator directly without an extra splash button.
    [Activity(Label = "Ryujinx Android", MainLauncher = true, Exported = true,
        NoHistory = true,
        ScreenOrientation = global::Android.Content.PM.ScreenOrientation.SensorLandscape)]
    public sealed class BootActivity : Activity
    {
        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            var intent = new Intent();
            intent.SetClass(this, typeof(MainActivity));
            StartActivity(intent);
            Finish();
        }
    }
}
