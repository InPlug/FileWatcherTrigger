using NetEti.ApplicationControl;
using NetEti.Globals;
using Vishnu.Interchange;

namespace FileWatcherTriggerDemo
{
    internal class Program
    {
        public static void Main(string[] args)
        {
            AppSettings _appSettings = GenericSingletonProvider.GetInstance<AppSettings>();
            string logFilePathName = "FileWatcherTriggerDemo.log";
            Logger logger = new Logger(logFilePathName, "", false);
            InfoController.GetInfoSource().RegisterInfoReceiver(logger, InfoTypes.Collection2InfoTypeArray(InfoTypes.All));
            string triggerParameters = @".\Testdatei.txt | Initial | S:30 | d:\tmp";
            FileWatcherTrigger.FileWatcherTrigger trigger = new FileWatcherTrigger.FileWatcherTrigger();
            Console.WriteLine(@"Trigger.Start {0}", triggerParameters);
            trigger.Start(null, triggerParameters, trigger_TriggerIt);
            Console.WriteLine("Stopp Trigger mit Enter");
            Console.ReadLine();
            trigger.Stop(null, trigger_TriggerIt);
            Console.WriteLine("Trigger gestoppt");
            Console.ReadLine();
            logger.Dispose();
        }

        static void trigger_TriggerIt(TreeEvent source)
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {source.Name} feuert.");
        }
    }
}