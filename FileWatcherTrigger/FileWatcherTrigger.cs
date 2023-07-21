using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Reactive.Linq;
using System.IO;
using System.Reactive;
using Vishnu.Interchange;
using NetEti.ApplicationControl;
using NetEti.Globals;
using System.Text.RegularExpressions;

namespace FileWatcherTrigger
{
    /// <summary>
    /// Löst bei Änderung an einer Datei (triggerParameters) das Event 'TriggerIt'
    /// aus. Implementiert die Schnittstelle 'INodeTrigger' aus 'LogicalTaskTree.dll', über
    /// die sich der LogicalTaskTree von 'Vishnu' in das Event einhängen und den Trigger
    /// starten und stoppen kann.
    /// </summary>
    /// <remarks>
    /// Autor: Erik Nagel
    ///
    /// 30.12.2013 Erik Nagel: erstellt.
    /// 29.03.2014 Erik Nagel: für die Verarbeitung von mehreren alternativen Pfaden erweitert.
    /// 25.06.2016 Erik Nagel: Try-Catch in OnTriggerFired und NULL-Check in Info;
    ///                        bei Exception alle FileSystemWatcher canceln, auf null setzen
    ///                        und neu aufsetzen.
    /// 25.07.2016 Erik Nagel: In foreach-Schleifen wegen thread-safety linq.ToList implementiert.
    /// 28.07.2016 Erik Nagel: ToList reicht nicht wg. Empty List Fehler - Lock und Copy implementiert.
    /// 13.08.2019 Erik Nagel: Zusätzlichen Timer implementiert.
    /// 27.06.2021 Erik Nagel: auf neue Basisklasse TriggerBase angepasst.
    /// </remarks>
    public class FileWatcherTrigger : TriggerBase, IDisposable
    {
        #region public members

        #region IDisposable Member

        private bool _disposed = false;

        /// <summary>
        /// Öffentliche Methode zum Aufräumen.
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Abschlussarbeiten, ggf. Timer zurücksetzen.
        /// </summary>
        /// <param name="disposing">False, wenn vom eigenen Destruktor aufgerufen.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (!this._disposed)
            {
                if (disposing)
                {
                    // Aufräumarbeiten durchführen und dann beenden.
                    try
                    {
                        this.DisposeFileSystemWatchers();
                    }
                    catch { }
                }
                this._disposed = true;
            }
        }

        private void DisposeFileSystemWatchers()
        {
            if (this._fileSystemWatchers != null)
            {
                lock (FileWatcherTrigger._lockMe)
                {
                    foreach (FileSystemWatcher fileSystemWatcher in this._fileSystemWatchers.Keys)
                    {
                        fileSystemWatcher?.Dispose();
                    }
                }
            }
            this._fileSystemWatchers?.Clear();
        }

        /// <summary>
        /// Destruktor
        /// </summary>
        ~FileWatcherTrigger()
        {
            this.Dispose(false);
        }

        #endregion IDisposable Member

        #region INodeTriggerImplementation

        /// <summary>
        /// Enthält weitergehende Informationen zum Trigger.
        /// Überschreibt TriggerBase.TriggerInfo um für diese Trigger-Variante
        /// spezifische Informationen auszugeben.
        /// </summary>
        public override TriggerInfo? Info
        {
            get
            {
                string info = "beobachtet ";
                string delimiter = "";
                if (this._fileSystemWatchers != null)
                {
                    foreach (FileSystemWatcherTriggerControl triggerControl
                      in DictionaryThreadSafeCopy<FileSystemWatcher, FileSystemWatcherTriggerControl>
                        .GetDictionaryValuesThreadSafeCopy(this._fileSystemWatchers))
                    {
                        if (triggerControl != null)
                        {
                            info = info + delimiter + Path.Combine(triggerControl.WatchedDirectory, triggerControl.WatchedFileName);
                            delimiter = " oder ";
                        }
                    }
                }
                if (this._eventTimer != null && this._eventTimer.Enabled)
                {
                    info = info + " oder: " + this._nextStart.ToString();
                    this._info.NextRun = this._nextStart;
                }
                else
                {
                    this._info.NextRun = DateTime.MinValue;
                }
                this._info.NextRunInfo = info;
                return this._info;
            }
            set
            {
            }
        }

        /// <summary>
        /// Startet den Trigger; vorher sollte sich der Consumer in TriggerIt eingehängt haben.
        /// </summary>
        /// <param name="triggerController">Das Objekt, das den Trigger aufruft.</param>
        /// <param name="triggerParameters">Zeit bis zum ersten Start und Intervall durch Pipe ('|') getrennt.
        /// Die Zeitangaben bestehen aus Einheit und Wert durch Doppelpunkt getrennt.
        /// Einheiten sind: "MS" Millisekunden, "S" Sekunden, "M" Minuten, "H" Stunden und "D" Tage.</param>
        /// <param name="triggerIt">Die aufzurufende Callback-Routine, wenn der Trigger feuert.</param>
        /// <returns>True, wenn der Trigger durch diesen Aufruf tatsächlich gestartet wurde.</returns>
        public override bool Start(object? triggerController, object? triggerParameters, Action<TreeEvent> triggerIt)
        {
            base.Start(triggerController, triggerParameters, triggerIt);

            // Trigger-spezifischer Code - Anfang
            if (this._eventTimer != null)
            {
                this._lastStart = DateTime.Now;
                this._nextStart = this._lastStart.AddMilliseconds(this._timerInterval);
                this._eventTimer.Start();
            }
            return this.setupTriggers();
            // Trigger-spezifischer Code - Ende
        }

        /// <summary>
        /// Stoppt den Trigger.
        /// </summary>
        /// <param name="triggerController">Das Objekt, das den Trigger definiert.</param>
        /// <param name="triggerIt">Die aufzurufende Callback-Routine, wenn der Trigger feuert.</param>
        public override void Stop(object? triggerController, Action<TreeEvent> triggerIt)
        {
            if (this._eventTimer != null)
            {
                this._eventTimer.Stop();
            }
            this.setControllerInfo(triggerController);
            foreach (FileSystemWatcherTriggerControl triggerControl
              in DictionaryThreadSafeCopy<FileSystemWatcher, FileSystemWatcherTriggerControl>
                .GetDictionaryValuesThreadSafeCopy(this._fileSystemWatchers))
            {
                if (triggerControl.WatcherTerminator != null)
                {
                    triggerControl.WatcherTerminator.Cancel();
                }
            }
            // Weitergabe des Stop-Aufrufs an die Basisklasse
            base.Stop(triggerController, triggerIt);

            this.Log("Watchers stopped!");
        }

        #endregion INodeTriggerImplementation

        /// <summary>
        /// Konstruktor - initialisiert die Liste von FileWatchern.
        /// </summary>
        public FileWatcherTrigger() : base()
        {
            this.TriggerName = "FileWatcherTrigger";
            this._me = ++FileWatcherTrigger._ids;
            this._validDirectories = new List<string>();
            this._fileSystemWatchers = new Dictionary<FileSystemWatcher, FileSystemWatcherTriggerControl>();
        }

        #endregion public members

        #region protected members

        /// <summary>
        /// Diese Routine wird von der Routine "Start" angesprungen, bevor der Trigger gestartet wird.
        /// Erweitert TriggerBase.EvaluateParametersOrFail; dort wird nur der Parameter "|UserRun"
        /// ausgewertet und die Variable "_isUserRun" entsprechend gesetzt.
        /// </summary>
        /// <param name="triggerParameters">Die von Vishnu weitergeleiteten Parameter aus der JobDescription.xml.</param>
        /// <param name="triggerController">Der Knoten, dem dieser Trigger zugeordnet ist.</param>
        protected override void EvaluateParametersOrFail(ref object? triggerParameters, object? triggerController)
        {
            base.EvaluateParametersOrFail(ref triggerParameters, triggerController);

            this.setControllerInfo(triggerController);
            string triggerParametersString = triggerParameters?.ToString() ?? "";
            this._eventTimer = null;
            this._lastStart = DateTime.MinValue;
            this._nextStart = DateTime.MinValue;
            this._info = new TriggerInfo() { NextRun = DateTime.MinValue, NextRunInfo = null };
            this._textPattern = @"(?:MS|S|M|H|D):\d+";
            this._compiledPattern = new Regex(_textPattern);

            MatchCollection alleTreffer;
            alleTreffer = _compiledPattern.Matches(triggerParametersString);
            this._timerInterval = 0;
            if (alleTreffer.Count > 0)
            {
                string subKey = alleTreffer[0].Groups[0].Value;
                triggerParametersString = triggerParametersString.Replace(subKey, "");
                switch (subKey.Split(':')[0])
                {
                    case "MS": this._timerInterval = Convert.ToInt32(subKey.Split(':')[1]); break;
                    case "S": this._timerInterval = Convert.ToInt32(subKey.Split(':')[1]) * 1000; break; ;
                    case "M": this._timerInterval = Convert.ToInt32(subKey.Split(':')[1]) * 1000 * 60; break; ;
                    case "H": this._timerInterval = Convert.ToInt32(subKey.Split(':')[1]) * 1000 * 60 * 60; break; ;
                    case "D": this._timerInterval = Convert.ToInt32(subKey.Split(':')[1]) * 1000 * 60 * 60 * 24; break; ;
                    default:
                        throw new ArgumentException("Falsche Einheit, zulässig sind: MS=Millisekunden, S=Sekunden, M=Minuten, H=Stunden, D=Tage.");
                }
                this._eventTimer = new System.Timers.Timer(this._timerInterval);
                this._eventTimer.Elapsed += new System.Timers.ElapsedEventHandler(eventTimer_Elapsed);
                this._eventTimer.Stop();
            }

            string[] para = (triggerParametersString + "|").Split('|');
            this._initialFire = false;
            string? firstValidFile = null;
            this._fileName = null;
            for (int i = 0; i < para.Count(); i++)
            {
                string paraString = para[i].Trim();
                if (paraString != "")
                {
                    if (paraString.ToUpper().StartsWith("INITIAL"))
                    {
                        this._initialFire = true;
                    }
                    else
                    {
                        if (firstValidFile == null)
                        {
                            if (this._fileName == null) // der erste Parameter ist inclusive Dateiname
                            {
                                this._fileName = Path.GetFileName(paraString);
                                paraString = Path.GetDirectoryName(paraString) ?? "";
                            }
                            foreach (string path in paraString.Split(','))
                            {
                                string workerPath = path.Trim(' ');
                                if (File.Exists(Path.Combine(workerPath, this._fileName)))
                                {
                                    firstValidFile = Path.Combine(workerPath, this._fileName);
                                    this._validDirectories.Clear();
                                    this._validDirectories.Add(workerPath);
                                    break;
                                }
                                if (Directory.Exists(workerPath))
                                {
                                    this._validDirectories.Add(workerPath);
                                }
                            }
                        }
                    }
                }
            }
            if (this._validDirectories.Count == 0)
            {
                throw new DirectoryNotFoundException(
                    String.Format("Es wurde kein gültiges Verzeichnis gefunden ({0}).",
                    triggerParameters?.ToString()));
            }
        }

        /// <summary>
        /// Diese Routine löst das Trigger-Event aus.
        /// </summary>
        protected void OnTriggerFired(FileSystemWatcher fileSystemWatcher, EventPattern<FileSystemEventArgs> ep)
        {
            this.Log("OnTriggerFired");
            if (this._eventTimer != null)
            {
                this._eventTimer.Stop();
            }
            try
            {
                if (fileSystemWatcher != null)
                {
                    fileSystemWatcher.EnableRaisingEvents = false;
                }
                Thread.Sleep(300); // Doppel-Events aussitzen. 15.07.2022 Nagel+- hierhin verschoben, damit evetuell
                                   // die Dateisystem-Operation noch abgeschlossen werden kann, bevor der Trigger feuert.
                base.OnTriggerFired(0);
                // 15.07.2022 Nagel+- auskommentiert: Thread.Sleep(300); // Doppel-Events aussitzen.
                if (fileSystemWatcher != null)
                {
                    fileSystemWatcher.EnableRaisingEvents = true;
                }
                if (this._eventTimer != null)
                {
                    this._lastStart = DateTime.Now;
                    this._nextStart = this._lastStart.AddMilliseconds(this._timerInterval);
                    this._eventTimer.Start();
                }
            }
            catch (Exception ex)
            {
                this.Log(String.Format("OnTriggerFired Exception: {0}", ex.Message));
                NotAccessibleError(fileSystemWatcher, new ErrorEventArgs(ex));
            }
        }

        /// <summary>
        /// Wird ausgeführt, wenn im FileSystemWatcher eine Exception aufgetreten ist, z.B.
        /// Error: Watched directory not accessible at 21.06.2016 18:16:29
        /// Nicht genügend Systemressourcen, um den angeforderten Dienst auszuführen
        /// </summary>
        /// <param name="source">Der FileSystemWatcher</param>
        /// <param name="e">Zusatzinformationen zum Fehler (enthält die Exception).</param>
        protected void OnError(object source, ErrorEventArgs e)
        {
            if (e.GetException().GetType() == typeof(InternalBufferOverflowException))
            {
                this.Log("Error: File System Watcher internal buffer overflow at "
                  + DateTime.Now + Environment.NewLine + e.GetException().Message);
            }
            else
            {
                this.Log("Error: Watched directory not accessible at "
                  + DateTime.Now + Environment.NewLine + e.GetException().Message);
            }
            NotAccessibleError((FileSystemWatcher)source, e);
        }

        #endregion protected members

        #region private members

        private Dictionary<FileSystemWatcher, FileSystemWatcherTriggerControl> _fileSystemWatchers;
        private List<string> _validDirectories;
        private static int _ids = 0;
        private int _me;
        private string? _controllerInfo;
        private static object _lockMe = new object();
        private bool _initialFire;
        private string? _fileName;

        private System.Timers.Timer? _eventTimer;
        private int _timerInterval;
        private string? _textPattern;
        private Regex? _compiledPattern;

        private class FileSystemWatcherTriggerControl
        {
            public IObservable<EventPattern<FileSystemEventArgs>>? WatcherTask { get; set; }
            public CancellationTokenSource? WatcherTerminator { get; set; }
            public string WatchedDirectory { get; set; }
            public string WatchedFileName { get; set; }

            public FileSystemWatcherTriggerControl()
            {
                this.WatchedDirectory = "";
                this.WatchedFileName = "";
            }
        }

        private bool setupTriggers()
        {
            lock (FileWatcherTrigger._lockMe)
            {
                foreach (string watchedDirectory in EnumerableThreadSafeCopy<string>
                          .GetEnumerableThreadSafeCopy(this._validDirectories))
                {
                    FileSystemWatcher? fileSystemWatcher = null;
                    FileSystemWatcherTriggerControl? control = null;
                    fileSystemWatcher
                      = new FileSystemWatcher
                      {
                          Path = watchedDirectory,
                          NotifyFilter = NotifyFilters.LastWrite,
                          Filter = this._fileName ?? throw new ApplicationException(
                              "Es wurde Kein Dateiname gefunden."),
                          IncludeSubdirectories = false,
                          EnableRaisingEvents = false
                      };
                    fileSystemWatcher.Error += new ErrorEventHandler(OnError);
                    IObservable<EventPattern<FileSystemEventArgs>> watcherTask =
                      Observable.FromEventPattern<FileSystemEventHandler, FileSystemEventArgs>(
                        h => fileSystemWatcher.Changed += h,
                        h => fileSystemWatcher.Changed -= h);
                    CancellationTokenSource watcherTerminator = new CancellationTokenSource();
                    CancellationToken token = watcherTerminator.Token;
                    control = new FileSystemWatcherTriggerControl()
                    {
                        WatcherTask = watcherTask,
                        WatcherTerminator = watcherTerminator,
                        WatchedDirectory = watchedDirectory,
                        WatchedFileName = this._fileName
                    };
                    this._fileSystemWatchers.Add(fileSystemWatcher, control);
                    token.Register(() => CancelNotification());
                    token.Register(watcherTask.Subscribe(ep => this.OnTriggerFired(fileSystemWatcher, ep)).Dispose);
                    fileSystemWatcher.EnableRaisingEvents = true;
                }
                if (this._initialFire && this._fileSystemWatchers.Count > 0)
                {
                    this.OnTriggerFired(this._fileSystemWatchers.First().Key, new EventPattern<FileSystemEventArgs>(this,
                      new FileSystemEventArgs(WatcherChangeTypes.Changed,
                        this._fileSystemWatchers.First().Value.WatchedDirectory,
                        this._fileSystemWatchers.First().Value.WatchedFileName)));
                }
            }
            this.Log("Watcher started");
            return true;
        }

        private void eventTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            this.OnTriggerFired(this._fileSystemWatchers.First().Key, new EventPattern<FileSystemEventArgs>(this,
              new FileSystemEventArgs(WatcherChangeTypes.Changed,
                this._fileSystemWatchers.First().Value.WatchedDirectory,
                this._fileSystemWatchers.First().Value.WatchedFileName)));
        }

        // Informiert über den Abbruch der Verarbeitung.
        private void CancelNotification()
        {
            this.Log("cancelNotification!");
        }

        private void NotAccessibleError(FileSystemWatcher source, ErrorEventArgs e)
        {
            this.Log("stopping triggers!");
            foreach (FileSystemWatcherTriggerControl triggerControl
              in DictionaryThreadSafeCopy<FileSystemWatcher, FileSystemWatcherTriggerControl>
                .GetDictionaryValuesThreadSafeCopy(this._fileSystemWatchers))
            {
                if (triggerControl.WatcherTerminator != null)
                {
                    triggerControl.WatcherTerminator.Cancel();
                    Thread.Sleep(100);
                }
            }
            this.DisposeFileSystemWatchers();
            this.Log("restarting triggers!");
            this.setupTriggers();
        }

        /// <summary>
        /// Erzeugt einen String mit Informationen über den aufrufenden
        /// TriggerController zu Logging-Zwecken.
        /// </summary>
        /// <param name="triggerController"></param>
        private void setControllerInfo(object? triggerController)
        {
            if (triggerController is IVishnuNode)
            {
                IVishnuNode infoNode = (IVishnuNode)triggerController;
                this._controllerInfo = string.Format("{0}/{1}, Type: {2}", infoNode.IdInfo, infoNode.NameInfo, infoNode.TypeInfo);
            }
            else
            {
                this._controllerInfo = "Unknown controller type";
            }
        }

        private void Log(string message)
        {
            TriggerInfo? info = this.Info;
            InfoController.Say(String.Format("#FWT#{0}, {1}, {2}\r\n{3}",
                this._me, this._controllerInfo, info?.NextRunInfo, message));
        }

        #endregion private members

    }
}
