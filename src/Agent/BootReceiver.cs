using System.IO;
using Android.App;
using Android.Content;
using Android.OS;

namespace PhoneFarm.Agent
{
    [BroadcastReceiver(Exported = true, Name = "pl.mz1.phonefarm.agent.BootReceiver")]
    public class BootReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context context, Intent intent)
        {
            if (!File.Exists(Path.Combine(context.FilesDir.Path, "enabled"))) return;
            var service = new Intent(context, typeof(AgentService));
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) context.StartForegroundService(service);
            else context.StartService(service);
        }
    }
}
