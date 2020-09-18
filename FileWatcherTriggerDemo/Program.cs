using System;
using Vishnu.Interchange;
using NetEti.ApplicationControl;
using NetEti.Globals;

namespace FileWatcherTriggerDemo
{
    public class Program
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
            Console.WriteLine("stop trigger mit enter");
            Console.ReadLine();
            trigger.Stop(null, trigger_TriggerIt);
            Console.WriteLine("Trigger stopped");
            Console.ReadLine();
            logger.Dispose();
        }

        static void trigger_TriggerIt(TreeEvent source)
        {
            Console.WriteLine("{0:HH:mm:ss} Trigger feuert.", DateTime.Now);
        }
    }
}
