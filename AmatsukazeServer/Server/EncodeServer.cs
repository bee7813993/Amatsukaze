﻿using Livet;
using Amatsukaze.Lib;
using Codeplex.Data;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Windows.Media.Imaging;

namespace Amatsukaze.Server
{
    public class Client : NotificationObject
    {
        private ClientManager manager;
        private TcpClient client;
        private NetworkStream stream;

        public string HostName { get; private set; }
        public int Port { get; private set; }

        public IPEndPoint RemoteIP {
            get {
                return (IPEndPoint)client.Client.RemoteEndPoint;
            }
        }

        public IPEndPoint LocalIP {
            get {
                return (IPEndPoint)client.Client.LocalEndPoint;
            }
        }

        #region TotalSendCount変更通知プロパティ
        private int _TotalSendCount;

        public int TotalSendCount {
            get { return _TotalSendCount; }
            set { 
                if (_TotalSendCount == value)
                    return;
                _TotalSendCount = value;
                RaisePropertyChanged();
            }
        }
        #endregion

        #region TotalRecvCount変更通知プロパティ
        private int _TotalRecvCount;

        public int TotalRecvCount {
            get { return _TotalRecvCount; }
            set { 
                if (_TotalRecvCount == value)
                    return;
                _TotalRecvCount = value;
                RaisePropertyChanged();
            }
        }
        #endregion

        public Client(TcpClient client, ClientManager manager)
        {
            this.manager = manager;
            this.client = client;
            this.stream = client.GetStream();

            var endPoint = (IPEndPoint)client.Client.RemoteEndPoint;
            HostName = Dns.GetHostEntry(endPoint.Address).HostName;
            Port = endPoint.Port;

            Util.AddLog("クライアント("+ HostName + ":" + Port + ")と接続");
        }

        public async Task Start()
        {
            try
            {
                while (true)
                {
                    var rpc = await RPCTypes.Deserialize(stream);
                    manager.OnRequestReceived(this, rpc.id, rpc.arg);
                    TotalRecvCount++;
                }
            }
            catch (Exception e)
            {
                Util.AddLog("クライアント(" + HostName + ":" + Port + ")との接続が切れました");
                Util.AddLog(e.Message);
                Close();
            }
            manager.OnClientClosed(this);
        }

        public void Close()
        {
            if (client != null)
            {
                client.Close();
                client = null;
            }
        }

        public NetworkStream GetStream()
        {
            return stream;
        }
    }

    public class ClientManager : NotificationObject, IUserClient
    {
        private TcpListener listener;
        private bool finished = false;
        private List<Task> receiveTask = new List<Task>();

        public ObservableCollection<Client> ClientList { get; private set; }

        private IEncodeServer server;

        public ClientManager(IEncodeServer server)
        {
            this.server = server;
            ClientList = new ObservableCollection<Client>();
        }

        public void Finish()
        {
            if (listener != null)
            {
                listener.Stop();
                listener = null;

                foreach (var client in ClientList)
                {
                    client.Close();
                }
            }
        }

        public async Task Listen(int port)
        {
            int errorCount = 0;

            while(finished == false)
            {
                listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                Util.AddLog("サーバ開始しました。ポート: " + port);

                try
                {
                    while (true)
                    {
                        var client = new Client(await listener.AcceptTcpClientAsync(), this);
                        receiveTask.Add(client.Start());
                        ClientList.Add(client);
                        errorCount = 0;
                    }
                }
                catch (Exception e)
                {
                    if (finished == false)
                    {
                        Util.AddLog("Listen中にエラーが発生");
                        Util.AddLog(e.Message);

                        // 一定時間待つ
                        await Task.Delay((++errorCount) * 5 * 1000);
                    }
                }
                finally
                {
                    try
                    {
                        listener.Stop();
                    }
                    catch { }
                }
            }
        }

        private async Task Send(RPCMethodId id, object obj)
        {
            byte[] bytes = RPCTypes.Serialize(id, obj);
            foreach (var client in ClientList.ToArray())
            {
                try
                {
                    await client.GetStream().WriteAsync(bytes, 0, bytes.Length);
                    client.TotalSendCount++;
                }
                catch (Exception e)
                {
                    Util.AddLog("クライアント(" +
                        client.HostName + ":" + client.Port + ")との接続が切れました");
                    Util.AddLog(e.Message);
                    client.Close();
                    OnClientClosed(client);
                }
            }
        }

        internal void OnRequestReceived(Client client, RPCMethodId methodId, object arg)
        {
            switch (methodId)
            {
                case RPCMethodId.SetProfile:
                    server.SetProfile((ProfileUpdate)arg);
                    break;
                case RPCMethodId.AddQueue:
                    server.AddQueue((AddQueueDirectory)arg);
                    break;
                case RPCMethodId.RemoveQueue:
                    server.RemoveQueue((int)arg);
                    break;
                case RPCMethodId.ChangeItem:
                    server.ChangeItem((ChangeItemData)arg);
                    break;
                case RPCMethodId.PauseEncode:
                    server.PauseEncode((bool)arg);
                    break;
                case RPCMethodId.SetCommonData:
                    server.SetCommonData((CommonData)arg);
                    break;
                case RPCMethodId.SetServiceSetting:
                    server.SetServiceSetting((ServiceSettingUpdate)arg);
                    break;
                case RPCMethodId.AddDrcsMap:
                    server.AddDrcsMap((DrcsImage)arg);
                    break;
                case RPCMethodId.EndServer:
                    server.EndServer();
                    break;
                case RPCMethodId.RequestSetting:
                    server.RequestSetting();
                    break;
                case RPCMethodId.RequestQueue:
                    server.RequestQueue();
                    break;
                case RPCMethodId.RequestLog:
                    server.RequestLog();
                    break;
                case RPCMethodId.RequestConsole:
                    server.RequestConsole();
                    break;
                case RPCMethodId.RequestLogFile:
                    server.RequestLogFile((LogItem)arg);
                    break;
                case RPCMethodId.RequestState:
                    server.RequestState();
                    break;
                case RPCMethodId.RequestFreeSpace:
                    server.RequestFreeSpace();
                    break;
                case RPCMethodId.RequestServiceSetting:
                    server.RequestServiceSetting();
                    break;
                case RPCMethodId.RequestLogoData:
                    server.RequestLogoData((string)arg);
                    break;
                case RPCMethodId.RequestDrcsImages:
                    server.RequestDrcsImages();
                    break;
            }
        }

        internal void OnClientClosed(Client client)
        {
            int index = ClientList.IndexOf(client);
            if (index >= 0)
            {
                receiveTask.RemoveAt(index);
                ClientList.RemoveAt(index);
            }
        }

        #region IUserClient
        public Task OnQueueData(QueueData data)
        {
            return Send(RPCMethodId.OnQueueData, data);
        }

        public Task OnQueueUpdate(QueueUpdate update)
        {
            return Send(RPCMethodId.OnQueueUpdate, update);
        }

        public Task OnLogData(LogData data)
        {
            return Send(RPCMethodId.OnLogData, data);
        }

        public Task OnLogUpdate(LogItem newLog)
        {
            return Send(RPCMethodId.OnLogUpdate, newLog);
        }

        public Task OnConsole(ConsoleData str)
        {
            return Send(RPCMethodId.OnConsole, str);
        }

        public Task OnConsoleUpdate(ConsoleUpdate str)
        {
            return Send(RPCMethodId.OnConsoleUpdate, str);
        }

        public Task OnLogFile(string str)
        {
            return Send(RPCMethodId.OnLogFile, str);
        }

        public Task OnCommonData(CommonData data)
        {
            return Send(RPCMethodId.OnCommonData, data);
        }

        public Task OnProfile(ProfileUpdate data)
        {
            return Send(RPCMethodId.OnProfile, data);
        }

        public Task OnServiceSetting(ServiceSettingUpdate service)
        {
            return Send(RPCMethodId.OnServiceSetting, service);
        }

        public Task OnLogoData(LogoData logoData)
        {
            return Send(RPCMethodId.OnLogoData, logoData);
        }

        public Task OnDrcsData(DrcsImageUpdate update)
        {
            return Send(RPCMethodId.OnDrcsData, update);
        }

        public Task OnAddResult(string requestId)
        {
            return Send(RPCMethodId.OnAddResult, requestId);
        }

        public Task OnOperationResult(string result)
        {
            return Send(RPCMethodId.OnOperationResult, result);
        }
        #endregion
    }

    public class EncodeServer : NotificationObject, IEncodeServer, IDisposable
    {
        [DataContract]
        private class AppData : IExtensibleDataObject
        {
            [DataMember]
            public Setting setting;
            [DataMember]
            public MakeScriptData scriptData;
            [DataMember]
            public ServiceSetting services;

            public ExtensionDataObject ExtensionData { get; set; }
        }

        private class ConsoleText : ConsoleTextBase
        {
            public List<string> TextLines = new List<string>();

            private int maxLines;

            public ConsoleText(int maxLines)
            {
                this.maxLines = maxLines;
            }

            public override void Clear()
            {
                base.Clear();
                TextLines.Clear();
            }

            public override void OnAddLine(string text)
            {
                if (TextLines.Count > maxLines)
                {
                    TextLines.RemoveRange(0, 100);
                }
                TextLines.Add(text);
            }

            public override void OnReplaceLine(string text)
            {
                if (TextLines.Count == 0)
                {
                    TextLines.Add(text);
                }
                else
                {
                    TextLines[TextLines.Count - 1] = text;
                }
            }
        }

        private class TranscodeTask
        {
            public TranscodeWorker thread;
            public QueueItem src;
            public FileStream logWriter;
            public Process process;
        }

        private class WorkerQueueItem
        {
            public QueueDirectory Dir;
            public QueueItem Item;
        }

        private class TranscodeWorker : IScheduleWorker<WorkerQueueItem>
        {
            public int id;
            public EncodeServer server;
            public ConsoleText logText;
            public ConsoleText consoleText;

            public TranscodeTask current { get; private set; }

            private List<Task> waitList;

            public void KillProcess()
            {
                if (current != null)
                {
                    if (current.process != null)
                    {
                        try
                        {
                            current.process.Kill();
                        }
                        catch (InvalidOperationException)
                        {
                            // プロセスが既に終了していた場合
                        }
                    }
                }
            }

            private LogItem FailLogItem(string srcpath, string reason, DateTime start, DateTime finish)
            {
                return new LogItem() {
                    Success = false,
                    Reason = reason,
                    SrcPath = srcpath,
                    MachineName = Dns.GetHostName(),
                    EncodeStartDate = start,
                    EncodeFinishDate = finish
                };
            }

            private Task WriteTextBytes(EncodeServer server, TranscodeTask transcode, byte[] buffer, int offset, int length)
            {
                if (transcode.logWriter != null)
                {
                    transcode.logWriter.Write(buffer, offset, length);
                }
                logText.AddBytes(buffer, offset, length);
                consoleText.AddBytes(buffer, offset, length);

                byte[] newbuf = new byte[length];
                Array.Copy(buffer, newbuf, length);
                return server.client.OnConsoleUpdate(new ConsoleUpdate() { index = id, data = newbuf });
            }

            private Task WriteTextBytes(EncodeServer server, TranscodeTask transcode, byte[] buffer)
            {
                return WriteTextBytes(server, transcode, buffer, 0, buffer.Length);
            }

            private async Task RedirectOut(EncodeServer server, TranscodeTask transcode, Stream stream)
            {
                try
                {
                    byte[] buffer = new byte[1024];
                    while (true)
                    {
                        var readBytes = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (readBytes == 0)
                        {
                            // 終了
                            return;
                        }
                        await WriteTextBytes(server, transcode, buffer, 0, readBytes);
                    }
                }
                catch (Exception e)
                {
                    Debug.Print("RedirectOut exception " + e.Message);
                }
            }

            private LogItem LogFromJson(bool isGeneric, string jsonpath, DateTime start, DateTime finish, QueueItem src, int outputMask)
            {
                var json = DynamicJson.Parse(File.ReadAllText(jsonpath));
                if (isGeneric)
                {
                    return new LogItem() {
                        Success = true,
                        SrcPath = json.srcpath,
                        OutPath = json.outpath,
                        SrcFileSize = (long)json.srcfilesize,
                        OutFileSize = (long)json.outfilesize,
                        MachineName = Dns.GetHostName(),
                        EncodeStartDate = start,
                        EncodeFinishDate = finish
                    };
                }
                var outpath = new List<string>();
                foreach (var file in json.outfiles)
                {
                    outpath.Add(file.path);
                    foreach (var sub in file.subs)
                    {
                        outpath.Add(sub);
                    }
                }
                var logofiles = new List<string>();
                foreach (var logo in json.logofiles)
                {
                    if (string.IsNullOrEmpty(logo) == false)
                    {
                        logofiles.Add(Path.GetFileName(logo));
                    }
                }
                int incident = (int)json.incident;
                return new LogItem() {
                    Success = (incident < 10),
                    Reason = (incident < 10) ? "" : "インシデントが多すぎます",
                    SrcPath = json.srcpath,
                    OutPath = outpath,
                    SrcFileSize = (long)json.srcfilesize,
                    IntVideoFileSize = (long)json.intvideofilesize,
                    OutFileSize = (long)json.outfilesize,
                    SrcVideoDuration = TimeSpan.FromSeconds(json.srcduration),
                    OutVideoDuration = TimeSpan.FromSeconds(json.outduration),
                    EncodeStartDate = start,
                    EncodeFinishDate = finish,
                    MachineName = Dns.GetHostName(),
                    AudioDiff = new AudioDiff() {
                        TotalSrcFrames = (int)json.audiodiff.totalsrcframes,
                        TotalOutFrames = (int)json.audiodiff.totaloutframes,
                        TotalOutUniqueFrames = (int)json.audiodiff.totaloutuniqueframes,
                        NotIncludedPer = json.audiodiff.notincludedper,
                        AvgDiff = json.audiodiff.avgdiff,
                        MaxDiff = json.audiodiff.maxdiff,
                        MaxDiffPos = json.audiodiff.maxdiffpos
                    },
                    Chapter = json.cmanalyze,
                    NicoJK = json.nicojk,
                    OutputMask = outputMask,
                    ServiceName = src.ServiceName,
                    ServiceId = src.ServiceId,
                    TsTime = src.TsTime,
                    LogoFiles = logofiles,
                    Incident = incident
                };
            }

            private async Task<LogItem> ProcessItem(EncodeServer server, QueueItem src, QueueDirectory dir)
            {
                DateTime now = DateTime.Now;

                if (File.Exists(src.Path) == false)
                {
                    return FailLogItem(src.Path, "入力ファイルが見つかりません", now, now);
                }

                ProfileSetting profile = dir.Profile;
                ServiceSettingElement serviceSetting = 
                    server.appData.services.ServiceMap[src.ServiceId];

                bool ignoreNoLogo = true;
                string[] logopaths = null;
                if (profile.DisableChapter == false)
                {
                    var logofiles = serviceSetting.LogoSettings
                        .Where(s => s.CanUse(src.TsTime))
                        .Select(s => s.FileName)
                        .ToArray();
                    if (logofiles.Length == 0)
                    {
                        // これは必要ないはず
                        src.FailReason = "ロゴ設定がありません";
                        return null;
                    }
                    ignoreNoLogo = !logofiles.All(path => path != LogoSetting.NO_LOGO);
                    logopaths = logofiles.Where(path => path != LogoSetting.NO_LOGO).ToArray();
                }


                // 出力パス生成
                string dstpath;
                if (dir.IsTest)
                {
                    var ext = (profile.OutputFormat == FormatType.MP4) ? ".mp4" : ".mkv";
                    var baseName = Path.Combine(dir.Encoded, Path.GetFileNameWithoutExtension(src.DstName));
                    dstpath = Util.CreateDstFile(baseName, ext);
                }
                else
                {
                    dstpath = Path.Combine(dir.Encoded, src.DstName);
                }

                bool isMp4 = src.Path.ToLower().EndsWith(".mp4");
                string srcpath = src.Path;
                string localsrc = null;
                string localdst = dstpath;
                string tmpBase = null;

                try
                {
                    if (dir.HashList != null || src.Hash != null)
                    {
                        // ハッシュがある（ネットワーク経由）の場合はローカルにコピー
                        // NASとエンコードPCが同じ場合はローカルでのコピーとなってしまうが
                        // そこだけ特別処理するのは大変なので、全部同じようにコピーする

                        tmpBase = Util.CreateTmpFile(server.appData.setting.WorkPath);
                        localsrc = tmpBase + "-in" + Path.GetExtension(srcpath);
                        string name = Path.GetFileName(srcpath);
                        if (dir.HashList != null && dir.HashList.ContainsKey(name) == false)
                        {
                            return FailLogItem(src.Path, "入力ファイルのハッシュがありません", now, now);
                        }

                        byte[] hash = await HashUtil.CopyWithHash(srcpath, localsrc);
                        var refhash = (dir.HashList != null) ? dir.HashList[name] : src.Hash;
                        if (hash.SequenceEqual(refhash) == false)
                        {
                            File.Delete(localsrc);
                            return FailLogItem(src.Path, "コピーしたファイルのハッシュが一致しません", now, now);
                        }

                        srcpath = localsrc;
                        localdst = tmpBase + "-out.mp4";
                    }

                    string json = Path.Combine(
                        Path.GetDirectoryName(localdst),
                        Path.GetFileNameWithoutExtension(localdst)) + "-enc.json";
                    string logpath = Path.Combine(
                        Path.GetDirectoryName(dstpath),
                        Path.GetFileNameWithoutExtension(dstpath)) + "-enc.log";
                    string jlscmd = serviceSetting.DisableCMCheck ?
                        null :
                        (string.IsNullOrEmpty(serviceSetting.JLSCommand) ?
                        profile.DefaultJLSCommand :
                        serviceSetting.JLSCommand);
                    string jlsopt = serviceSetting.DisableCMCheck ?
                        null : serviceSetting.JLSOption;

                    string args = server.MakeAmatsukazeArgs(
                        profile,
                        server.appData.setting,
                        isMp4,
                        srcpath, localdst, json,
                        src.ServiceId, logopaths, ignoreNoLogo, jlscmd, jlsopt);
                    string exename = server.appData.setting.AmatsukazePath;

                    int outputMask = profile.OutputMask;

                    Util.AddLog(id, "エンコード開始: " + src.Path);
                    Util.AddLog(id, "Args: " + exename + " " + args);

                    DateTime start = DateTime.Now;

                    var psi = new ProcessStartInfo(exename, args) {
                        UseShellExecute = false,
                        WorkingDirectory = Directory.GetCurrentDirectory(),
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        RedirectStandardInput = false,
                        CreateNoWindow = true
                    };

                    int exitCode = -1;
                    bool isCanceled = false;
                    logText.Clear();

                    try
                    {
                        using (var p = Process.Start(psi))
                        {
                            try
                            {
                                // アフィニティを設定
                                IntPtr affinityMask = new IntPtr((long)server.affinityCreator.GetMask(id));
                                Util.AddLog(id, "AffinityMask: " + affinityMask.ToInt64());
                                p.ProcessorAffinity = affinityMask;
                                p.PriorityClass = ProcessPriorityClass.BelowNormal;
                            }
                            catch (InvalidOperationException)
                            {
                                // 既にプロセスが終了していると例外が出るが無視する
                            }

                            current = new TranscodeTask() {
                                thread = this,
                                src = src,
                                process = p,
                            };

                            using (current.logWriter = File.Create(logpath))
                            {
                                // 起動コマンドをログ出力
                                await WriteTextBytes(server, current, Encoding.Default.GetBytes(exename + " " + args + "\n"));

                                await Task.WhenAll(
                                    RedirectOut(server, current, p.StandardOutput.BaseStream),
                                    RedirectOut(server, current, p.StandardError.BaseStream),
                                    Task.Run(() =>
                                    {
                                        while(p.WaitForExit(1000) == false)
                                        {
                                            if(src.State == QueueState.Canceled)
                                            {
                                                // キャンセルされた
                                                p.Kill();
                                                isCanceled = true;
                                                break;
                                            }
                                        }
                                    }));
                            }

                            exitCode = p.ExitCode;
                        }
                    }
                    catch (Win32Exception w32e)
                    {
                        Util.AddLog(id, "Amatsukazeプロセス起動に失敗");
                        throw w32e;
                    }
                    catch (IOException ioe)
                    {
                        Util.AddLog(id, "ログファイル生成に失敗");
                        throw ioe;
                    }
                    finally
                    {
                        if (current != null)
                        {
                            current.logWriter = null;
                            current = null;
                        }
                    }

                    DateTime finish = DateTime.Now;

                    if (dir.HashList != null || src.Hash != null)
                    {
                        File.Delete(localsrc);
                    }

                    // ログを整形したテキストに置き換える
                    using (var fs = new StreamWriter(File.Create(logpath), Encoding.Default))
                    {
                        foreach (var str in logText.TextLines)
                        {
                            fs.WriteLine(str);
                        }
                    }

                    // ログファイルを専用フォルダにコピー
                    if (File.Exists(logpath))
                    {
                        string logbase = server.GetLogFileBase(start);
                        Directory.CreateDirectory(Path.GetDirectoryName(logbase));
                        string dstlog = logbase + ".txt";
                        File.Copy(logpath, dstlog);

                        if (File.Exists(json))
                        {
                            string dstjson = logbase + ".json";
                            File.Move(json, dstjson);
                            json = dstjson;
                        }
                    }

                    if(exitCode == 0 && isCanceled == false)
                    {
                        // 成功
                        var log = LogFromJson(isMp4, json, start, finish, src, outputMask);

                        // ハッシュがある（ネットワーク経由）の場合はリモートにコピー
                        if (dir.HashList != null || src.Hash != null)
                        {
                            log.SrcPath = src.Path;
                            string localbase = Path.GetDirectoryName(localdst) + "\\" + Path.GetFileNameWithoutExtension(localdst);
                            string outbase = Path.GetDirectoryName(dstpath) + "\\" + Path.GetFileNameWithoutExtension(dstpath);
                            for (int i = 0; i < log.OutPath.Count; ++i)
                            {
                                string outpath = outbase + log.OutPath[i].Substring(localbase.Length);
                                var hash = await HashUtil.CopyWithHash(log.OutPath[i], outpath);
                                string name = Path.GetFileName(outpath);
                                HashUtil.AppendHash(Path.Combine(dir.Encoded, "_encoded.hash"), name, hash);
                                File.Delete(log.OutPath[i]);
                                log.OutPath[i] = outpath;
                            }
                        }

                        return log;
                    }

                    // 失敗 //

                    if (dir.IsTest)
                    {
                        // 出力ファイルを削除
                        File.Delete(dstpath);
                    }

                    if(isCanceled)
                    {
                        // キャンセルされた
                        return FailLogItem(src.Path, "キャンセルされました", start, finish);
                    }
                    else if (exitCode == 100)
                    {
                        // マッチするロゴがなかった
                        return FailLogItem(src.Path, "マッチするロゴがありませんでした", start, finish);
                    }
                    else if (exitCode == 101)
                    {
                        // DRCSマッピングがなかった
                        return FailLogItem(src.Path, "DRCS外字のマッピングがありませんでした", start, finish);
                    }
                    else
                    {
                        // その他
                        return FailLogItem(src.Path,
                            "Amatsukaze.exeはコード" + exitCode + "で終了しました。", start, finish);
                    }

                }
                finally
                {
                    if (tmpBase != null)
                    {
                        File.Delete(tmpBase);
                    }
                }
            }

            private async Task<bool> RunEncodeItem(WorkerQueueItem workerItem)
            {
                try
                {
                    var dir = workerItem.Dir;
                    var src = workerItem.Item;

                    // キューじゃなかったらダメ
                    if (src.State != QueueState.Queue)
                    {
                        return true;
                    }

                    if(dir.IsBatch)
                    {
                        Directory.CreateDirectory(dir.Succeeded);
                        Directory.CreateDirectory(dir.Failed);
                        Directory.CreateDirectory(dir.Encoded);
                    }

                    // 待たなくてもいいタスクリスト
                    waitList = new List<Task>();

                    LogItem logItem = null;
                    bool result = true;

                    server.UpdateQueueItem(src, dir, false);
                    if (src.State == QueueState.Queue)
                    {
                        src.State = QueueState.Encoding;
                        waitList.Add(server.NotifyQueueItemUpdate(src, dir));
                        logItem = await ProcessItem(server, src, dir);
                    }

                    if (logItem == null)
                    {
                        // ペンディング
                        src.State = QueueState.LogoPending;
                        // 他の項目も更新しておく
                        waitList.AddRange(server.UpdateQueueItems());
                    }
                    else
                    {
                        if (logItem.Success)
                        {
                            src.State = QueueState.Complete;
                        }
                        else
                        {
                            src.State = QueueState.Failed;
                            src.FailReason = logItem.Reason;
                            result = false;
                        }

                        if (dir.IsBatch)
                        {
                            var sameItems = dir.Items.Where(s => s.Path == src.Path);
                            if (sameItems.Any(s => s.IsActive) == false)
                            {
                                // もうこのファイルでアクティブなアイテムはない
                                if (sameItems.Any(s => s.State == QueueState.Failed))
                                {
                                    // 失敗がある
                                    EncodeServer.MoveTSFile(src.Path, dir.Failed, dir.Profile.MoveEDCBFiles);
                                }
                                else
                                {
                                    // 全て成功
                                    EncodeServer.MoveTSFile(src.Path, dir.Succeeded, dir.Profile.MoveEDCBFiles);
                                }

                                // 関連ファイルをコピー
                                if(dir.Profile.MoveEDCBFiles)
                                {
                                    var commonName = Path.GetFileNameWithoutExtension(src.Path);
                                    var srcBody = Path.GetDirectoryName(src.Path) + "\\" + commonName;
                                    var dstBody = dir.Succeeded + "\\" + commonName;
                                    foreach (var ext in EncodeServer
                                        .GetFileExtentions(null, dir.Profile.MoveEDCBFiles))
                                    {
                                        var srcPath = srcBody + ext;
                                        var dstPath = dstBody + ext;
                                        if(File.Exists(srcPath) && !File.Exists(dstPath))
                                        {
                                            File.Copy(srcPath, dstPath);
                                        }
                                    }
                                }
                            }
                        }

                        server.log.Items.Add(logItem);
                        server.WriteLog();
                        waitList.Add(server.client.OnLogUpdate(server.log.Items.Last()));
                    }

                    waitList.Add(server.NotifyQueueItemUpdate(src, dir));
                    waitList.Add(server.RequestFreeSpace());

                    await Task.WhenAll(waitList);

                    return result;

                }
                catch (Exception e)
                {
                    await server.client.OnOperationResult("予期せぬエラー: " + e.Message);
                    return false;
                }
            }

            private async Task<bool> ProcessSearchItem(EncodeServer server, QueueItem src)
            {
                if (File.Exists(src.Path) == false)
                {
                    return false;
                }

                string args = server.MakeAmatsukazeSearchArgs(
                    src.Path, src.ServiceId);
                string exename = server.appData.setting.AmatsukazePath;

                Util.AddLog(id, "サーチ開始: " + src.Path);
                Util.AddLog(id, "Args: " + exename + " " + args);

                var psi = new ProcessStartInfo(exename, args)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Directory.GetCurrentDirectory(),
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = false,
                    CreateNoWindow = true
                };

                int exitCode = -1;
                logText.Clear();

                try
                {
                    using (var p = Process.Start(psi))
                    {
                        try
                        {
                            p.PriorityClass = ProcessPriorityClass.BelowNormal;
                        }
                        catch (InvalidOperationException)
                        {
                            // 既にプロセスが終了していると例外が出るが無視する
                        }

                        current = new TranscodeTask()
                        {
                            thread = this,
                            src = src,
                            process = p,
                        };

                        // 起動コマンドをログ出力
                        await WriteTextBytes(server, current, Encoding.Default.GetBytes(exename + " " + args + "\n"));

                        await Task.WhenAll(
                            RedirectOut(server, current, p.StandardOutput.BaseStream),
                            RedirectOut(server, current, p.StandardError.BaseStream),
                            Task.Run(() => p.WaitForExit()));

                        exitCode = p.ExitCode;
                    }

                    if (exitCode == 0)
                    {
                        return true;
                    }
                    else
                    {
                        return false;
                    }
                }
                catch (Win32Exception w32e)
                {
                    Util.AddLog(id, "Amatsukazeプロセス起動に失敗");
                    throw w32e;
                }
                finally
                {
                    current = null;
                }
            }

            private async Task<bool> RunSearchItem(WorkerQueueItem workerItem)
            {
                try
                {
                    var dir = workerItem.Dir;
                    var src = workerItem.Item;

                    // キューじゃなかったらダメ
                    if (src.State != QueueState.Queue)
                    {
                        return true;
                    }

                    // 待たなくてもいいタスクリスト
                    waitList = new List<Task>();

                    src.State = QueueState.Encoding;
                    waitList.Add(server.NotifyQueueItemUpdate(src, dir));
                    bool result = await ProcessSearchItem(server, src);
                    src.State = result ? QueueState.Complete : QueueState.Failed;
                    waitList.Add(server.NotifyQueueItemUpdate(src, dir));

                    await Task.WhenAll(waitList);

                    return result;

                }
                catch (Exception e)
                {
                    await server.client.OnOperationResult("予期せぬエラー: " + e.Message);
                    return false;
                }
            }

            public Task<bool> RunItem(WorkerQueueItem workerItem)
            {
                if(workerItem.Dir.Mode == ProcMode.DrcsSearch)
                {
                    return RunSearchItem(workerItem);
                }
                else
                {
                    return RunEncodeItem(workerItem);
                }
            }
        }

        private class EncodeException : Exception
        {
            public EncodeException(string message)
                : base(message)
            {
            }
        }

        private IUserClient client;
        public Task ServerTask { get; private set; }
        private AppData appData;

        private Action finishRequested;

        private EncodeScheduler<WorkerQueueItem> scheduler = null;

        private List<QueueDirectory> queue = new List<QueueDirectory>();
        private int nextDirId = 1;
        private int nextItemId = 1;
        private LogData log;
        private SortedDictionary<string, DiskItem> diskMap = new SortedDictionary<string, DiskItem>();

        private AffinityCreator affinityCreator = new AffinityCreator();

        private Dictionary<string, ProfileSetting> profiles = new Dictionary<string, ProfileSetting>();
        private List<string> JlsCommandFiles = new List<string>();
        private List<string> MainScriptFiles = new List<string>();
        private List<string> PostScriptFiles = new List<string>();
        private Dictionary<string, BitmapFrame> drcsImageCache = new Dictionary<string, BitmapFrame>();
        private Dictionary<string, DrcsImage> drcsMap = new Dictionary<string, DrcsImage>();

        // キューに追加されるTSを解析するスレッド
        private Task queueThread;
        private BufferBlock<AddQueueDirectory> queueQ = new BufferBlock<AddQueueDirectory>();

        // ロゴファイルやJLSコマンドファイルを監視するスレッド
        private Task watchFileThread;
        private BufferBlock<int> watchFileQ = new BufferBlock<int>();
        private bool serviceListUpdated;

        // 設定を保存するスレッド
        private Task saveSettingThread;
        private BufferBlock<int> saveSettingQ = new BufferBlock<int>();
        private bool settingUpdated;

        private PreventSuspendContext preventSuspend;

        #region EncodePaused変更通知プロパティ
        private bool encodePaused = false;

        public bool EncodePaused {
            get { return encodePaused; }
            set {
                if (encodePaused == value)
                    return;
                encodePaused = value;
                RaisePropertyChanged();
            }
        }
        #endregion

        #region NowEncoding変更通知プロパティ
        private bool nowEncoding = false;

        public bool NowEncoding {
            get { return nowEncoding; }
            set {
                if (nowEncoding == value)
                    return;
                nowEncoding = value;
                RaisePropertyChanged();
            }
        }
        #endregion

        public ClientManager ClientManager {
            get { return client as ClientManager; }
        }

        public EncodeServer(int port, IUserClient client, Action finishRequested)
        {
            this.finishRequested = finishRequested;
            LoadAppData();
            if (client != null)
            {
                this.client = client;
            }
            else
            {
                var clientManager = new ClientManager(this);
                ServerTask = clientManager.Listen(port);
                this.client = clientManager;
                RaisePropertyChanged("ClientManager");
            }
            ReadLog();

            scheduler = new EncodeScheduler<WorkerQueueItem>() {
                NewWorker = id => new TranscodeWorker() {
                    id = id,
                    server = this,
                    logText = new ConsoleText(1 * 1024 * 1024),
                    consoleText = new ConsoleText(500),
                },
                OnStart = () => {
                    if(!Directory.Exists(GetDRCSDirectoryPath()))
                    {
                        Directory.CreateDirectory(GetDRCSDirectoryPath());
                    }
                    if(!File.Exists(GetDRCSMapPath()))
                    {
                        File.Create(GetDRCSMapPath());
                    }
                    if (appData.setting.ClearWorkDirOnStart)
                    {
                        CleanTmpDir();
                    }
                    NowEncoding = true;
                    return RequestState();
                },
                OnFinish = ()=> {
                    NowEncoding = false;
                    var task = RequestState();
                    if (preventSuspend != null)
                    {
                        preventSuspend.Dispose();
                        preventSuspend = null;
                        if(appData.setting.FinishAction != FinishAction.None)
                        {
                            if (WinAPI.GetLastInputTime().Minutes >= 3)
                            {
                                // ユーザ操作が3分以上なかったらPCをサスペンド
                                var state = (appData.setting.FinishAction == FinishAction.Suspend)
                                        ? System.Windows.Forms.PowerState.Suspend
                                        : System.Windows.Forms.PowerState.Hibernate;
                                System.Windows.Forms.Application.SetSuspendState(state, false, false);
                            }
                        }
                    }
                    return task;
                },
                OnError = message => AddEncodeLog(message)
            };
            scheduler.SetNumParallel(appData.setting.NumParallel);
            affinityCreator.NumProcess = appData.setting.NumParallel;

            queueThread = QueueThread();
            watchFileThread = WatchFileThread();
            saveSettingThread = SaveSettingThread();
        }

        #region IDisposable Support
        private bool disposedValue = false; // 重複する呼び出しを検出するには

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: マネージ状態を破棄します (マネージ オブジェクト)。

                    // 終了時にプロセスが残らないようにする
                    if (scheduler != null)
                    {
                        foreach (var worker in scheduler.Workers.Cast<TranscodeWorker>())
                        {
                            if (worker != null)
                            {
                                worker.KillProcess();
                            }
                        }
                        scheduler.Finish();
                    }

                    queueQ.Complete();
                    watchFileQ.Complete();
                    saveSettingQ.Complete();

                    if (settingUpdated)
                    {
                        settingUpdated = false;
                        SaveAppData();
                    }
                }

                // TODO: アンマネージ リソース (アンマネージ オブジェクト) を解放し、下のファイナライザーをオーバーライドします。
                // TODO: 大きなフィールドを null に設定します。

                disposedValue = true;
            }
        }

        // TODO: 上の Dispose(bool disposing) にアンマネージ リソースを解放するコードが含まれる場合にのみ、ファイナライザーをオーバーライドします。
        // ~EncodeServer() {
        //   // このコードを変更しないでください。クリーンアップ コードを上の Dispose(bool disposing) に記述します。
        //   Dispose(false);
        // }

        // このコードは、破棄可能なパターンを正しく実装できるように追加されました。
        public void Dispose()
        {
            // このコードを変更しないでください。クリーンアップ コードを上の Dispose(bool disposing) に記述します。
            Dispose(true);
            // TODO: 上のファイナライザーがオーバーライドされる場合は、次の行のコメントを解除してください。
            // GC.SuppressFinalize(this);
        }
        #endregion

        #region Path
        private string GetSettingFilePath()
        {
            return "config\\AmatsukazeServer.xml";
        }

        private string GetHistoryFilePath()
        {
            return "data\\EncodeHistory.xml";
        }

        private string GetLogFileBase(DateTime start)
        {
            return "data\\logs\\" + start.ToString("yyyy-MM-dd_HHmmss.fff");
        }

        private string ReadLogFIle(DateTime start)
        {
            var logpath = GetLogFileBase(start) + ".txt";
            if (File.Exists(logpath) == false)
            {
                return "ログファイルが見つかりません。パス: " + logpath;
            }
            return File.ReadAllText(logpath, Encoding.Default);
        }

        private string GetLogoDirectoryPath()
        {
            return Path.GetFullPath("logo");
        }

        private string GetLogoFilePath(string fileName)
        {
            return GetLogoDirectoryPath() + "\\" + fileName;
        }

        private string GetJLDirectoryPath()
        {
            return Path.GetFullPath("JL");
        }

        private string GetAvsDirectoryPath()
        {
            return Path.GetFullPath("avs");
        }

        private string GetDRCSDirectoryPath()
        {
            return Path.GetFullPath("drcs");
        }

        private string GetDRCSImagePath(string md5)
        {
            return GetDRCSDirectoryPath() + "\\" + md5 + ".bmp";
        }

        private string GetDRCSMapPath()
        {
            return GetDRCSDirectoryPath() + "\\drcs_map.txt";
        }

        private string GetProfileDirectoryPath()
        {
            return Path.GetFullPath("profile");
        }

        private string GetProfilePath(string dirpath, string name)
        {
            return dirpath + "\\" + name + ".profile";
        }
        #endregion

        public void Finish()
        {
            if (client != null)
            {
                client.Finish();
                client = null;
            }
        }

        private Task NotifyMessage(string message, bool log)
        {
            if(log)
            {
                Util.AddLog(message);
            }
            return client.OnOperationResult(message);
        }

        private static string GetExePath(string basePath, string pattern)
        {
            foreach (var path in Directory.GetFiles(basePath))
            {
                var fname = Path.GetFileName(path);
                if (fname.StartsWith(pattern) && fname.EndsWith(".exe"))
                {
                    return path;
                }
            }
            return null;
        }

        private Setting SetDefaultPath(Setting setting)
        {
            string basePath = Path.GetDirectoryName(GetType().Assembly.Location);
            if (string.IsNullOrEmpty(setting.AmatsukazePath))
            {
                setting.AmatsukazePath = Path.Combine(basePath, "AmatsukazeCLI.exe");
            }
            if (string.IsNullOrEmpty(setting.X264Path))
            {
                setting.X264Path = GetExePath(basePath, "x264");
            }
            if (string.IsNullOrEmpty(setting.X265Path))
            {
                setting.X265Path = GetExePath(basePath, "x265");
            }
            if (string.IsNullOrEmpty(setting.MuxerPath))
            {
                setting.MuxerPath = Path.Combine(basePath, "muxer.exe");
            }
            if (string.IsNullOrEmpty(setting.MKVMergePath))
            {
                setting.MKVMergePath = Path.Combine(basePath, "mkvmerge.exe");
            }
            if (string.IsNullOrEmpty(setting.MP4BoxPath))
            {
                setting.MP4BoxPath = Path.Combine(basePath, "mp4box.exe");
            }
            if (string.IsNullOrEmpty(setting.TimelineEditorPath))
            {
                setting.TimelineEditorPath = Path.Combine(basePath, "timelineeditor.exe");
            }
            if (string.IsNullOrEmpty(setting.ChapterExePath))
            {
                setting.ChapterExePath = GetExePath(basePath, "chapter_exe");
            }
            if (string.IsNullOrEmpty(setting.JoinLogoScpPath))
            {
                setting.JoinLogoScpPath = GetExePath(basePath, "join_logo_scp");
            }
            return setting;
        }

        private Setting GetDefaultSetting()
        {
            string basePath = Path.GetDirectoryName(GetType().Assembly.Location);
            return SetDefaultPath(new Setting() { NumParallel = 1 });
        }

        private void LoadAppData()
        {
            string path = GetSettingFilePath();
            if (File.Exists(path) == false)
            {
                appData = new AppData() {
                    setting = GetDefaultSetting(),
                    scriptData = new MakeScriptData(),
                    services = new ServiceSetting() {
                        ServiceMap = new Dictionary<int, ServiceSettingElement>()
                    }
                };
                return;
            }
            using (FileStream fs = new FileStream(path, FileMode.Open))
            {
                var s = new DataContractSerializer(typeof(AppData));
                appData = (AppData)s.ReadObject(fs);
                if (appData.setting == null)
                {
                    appData.setting = GetDefaultSetting();
                }
                if (appData.scriptData == null)
                {
                    appData.scriptData = new MakeScriptData();
                }
                if (appData.services == null)
                {
                    appData.services = new ServiceSetting();
                }
                if (appData.services.ServiceMap == null)
                {
                    appData.services.ServiceMap = new Dictionary<int, ServiceSettingElement>();
                }
            }
        }

        private void SaveAppData()
        {
            string path = GetSettingFilePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (FileStream fs = new FileStream(path, FileMode.Create))
            {
                var s = new DataContractSerializer(typeof(AppData));
                s.WriteObject(fs, appData);
            }
        }

        private ProfileSetting ReadProfile(string filepath)
        {
            using (FileStream fs = new FileStream(filepath, FileMode.Open))
            {
                var s = new DataContractSerializer(typeof(ProfileSetting));
                var profile = (ProfileSetting)s.ReadObject(fs);
                if (profile.Bitrate == null)
                {
                    profile.Bitrate = new BitrateSetting();
                }
                if (profile.NicoJKFormats == null)
                {
                    profile.NicoJKFormats = new bool[4] { true, false, false, false };
                }
                return profile;
            }
        }

        private void SaveProfile(string filepath, ProfileSetting profile)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filepath));
            using (FileStream fs = new FileStream(filepath, FileMode.Create))
            {
                var s = new DataContractSerializer(typeof(ProfileSetting));
                s.WriteObject(fs, profile);
            }
        }

        private void ReadLog()
        {
            string path = GetHistoryFilePath();
            if (File.Exists(path) == false)
            {
                log = new LogData() {
                    Items = new List<LogItem>()
                };
                return;
            }
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open))
                {
                    var s = new DataContractSerializer(typeof(LogData));
                    log = (LogData)s.ReadObject(fs);
                    if (log.Items == null)
                    {
                        log.Items = new List<LogItem>();
                    }
                }
            }
            catch (IOException e)
            {
                Util.AddLog("ログファイルの読み込みに失敗: " + e.Message);
            }
        }

        private void WriteLog()
        {
            string path = GetHistoryFilePath();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (FileStream fs = new FileStream(path, FileMode.Create))
                {
                    var s = new DataContractSerializer(typeof(LogData));
                    s.WriteObject(fs, log);
                }
            }
            catch (IOException e)
            {
                Util.AddLog("ログファイル書き込み失敗: " + e.Message);
            }
        }

        private static string GetEncoderPath(EncoderType encoderType, Setting setting)
        {
            if (encoderType == EncoderType.x264)
            {
                return setting.X264Path;
            }
            else if (encoderType == EncoderType.x265)
            {
                return setting.X265Path;
            }
            else if (encoderType == EncoderType.QSVEnc)
            {
                return setting.QSVEncPath;
            }
            else
            {
                return setting.NVEncPath;
            }
        }

        private string GetEncoderName(EncoderType encoderType)
        {
            if (encoderType == EncoderType.x264)
            {
                return "x264";
            }
            else if (encoderType == EncoderType.x265)
            {
                return "x265";
            }
            else if (encoderType == EncoderType.QSVEnc)
            {
                return "QSVEnc";
            }
            else
            {
                return "NVEnc";
            }
        }

        private string MakeAmatsukazeSearchArgs(string src, int serviceId)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("--mode drcs")
                .Append(" --subtitles")
                .Append(" -i \"")
                .Append(src)
                .Append("\" -s ")
                .Append(serviceId)
                .Append(" --drcs \"")
                .Append(GetDRCSMapPath())
                .Append("\"");

            return sb.ToString();
        }

        private string MakeAmatsukazeArgs(
            ProfileSetting profile,
            Setting setting,
            bool isGeneric,
            string src, string dst, string json,
            int serviceId, string[] logofiles,
            bool ignoreNoLogo, string jlscommand, string jlsopt)
        {
            string encoderPath = GetEncoderPath(profile.EncoderType, setting);

            double bitrateCM = profile.BitrateCM;
            if (bitrateCM == 0)
            {
                bitrateCM = 1;
            }

            int outputMask = profile.OutputMask;
            if(outputMask == 0)
            {
                outputMask = 1;
            }

            StringBuilder sb = new StringBuilder();
            if (isGeneric)
            {
                sb.Append("--mode g ");
            }
            sb.Append("-i \"")
                .Append(src)
                .Append("\" -o \"")
                .Append(dst)
                .Append("\" -w \"")
                .Append(setting.WorkPath)
                .Append("\" -et ")
                .Append(GetEncoderName(profile.EncoderType))
                .Append(" -e \"")
                .Append(encoderPath)
                .Append("\" -j \"")
                .Append(json)
                .Append("\" --chapter-exe \"")
                .Append(setting.ChapterExePath)
                .Append("\" --jls \"")
                .Append(setting.JoinLogoScpPath)
                .Append("\" -s ")
                .Append(serviceId)
                .Append(" --cmoutmask ")
                .Append(outputMask)
                .Append(" --drcs \"")
                .Append(GetDRCSMapPath())
                .Append("\"");

            if (profile.OutputFormat == FormatType.MP4)
            {
                sb.Append(" --mp4box \"")
                    .Append(setting.MP4BoxPath)
                    .Append("\" -t \"")
                    .Append(setting.TimelineEditorPath)
                    .Append("\"");
            }

            if (string.IsNullOrEmpty(profile.EncoderOption) == false)
            {
                sb.Append(" -eo \"")
                    .Append(profile.EncoderOption)
                    .Append("\"");
            }

            if (profile.OutputFormat == FormatType.MP4)
            {
                sb.Append(" -fmt mp4 -m \"" + setting.MuxerPath + "\"");
            }
            else
            {
                sb.Append(" -fmt mkv -m \"" + setting.MKVMergePath + "\"");
            }

            if (bitrateCM != 1)
            {
                sb.Append(" -bcm ").Append(bitrateCM);
            }
            if(profile.SplitSub)
            {
                sb.Append(" --splitsub");
            }
            if (!profile.DisableChapter)
            {
                sb.Append(" --chapter");
            }
            if (!profile.DisableSubs)
            {
                sb.Append(" --subtitles");
            }
            if (profile.EnableNicoJK)
            {
                sb.Append(" --nicojk");
                if(profile.IgnoreNicoJKError)
                {
                    sb.Append(" --ignore-nicojk-error");
                }
                if (profile.NicoJK18)
                {
                    sb.Append(" --nicojk18");
                }
                sb.Append(" --nicojkmask ")
                    .Append(profile.NicoJKFormatMask);
                sb.Append(" --nicoass \"")
                    .Append(setting.NicoConvASSPath)
                    .Append("\"");
            }

            if (string.IsNullOrEmpty(jlscommand) == false)
            {
                sb.Append(" --jls-cmd \"")
                    .Append(GetJLDirectoryPath() + "\\" + jlscommand)
                    .Append("\"");
            }
            if (string.IsNullOrEmpty(jlsopt) == false)
            {
                sb.Append(" --jls-option \"")
                    .Append(jlsopt)
                    .Append("\"");
            }

            if (string.IsNullOrEmpty(profile.FilterPath) == false)
            {
                sb.Append(" -f \"")
                    .Append(GetAvsDirectoryPath() + "\\" + profile.FilterPath)
                    .Append("\"");
            }

            if (string.IsNullOrEmpty(profile.PostFilterPath) == false)
            {
                sb.Append(" -pf \"")
                    .Append(GetAvsDirectoryPath() + "\\" + profile.PostFilterPath)
                    .Append("\"");
            }

            if (profile.AutoBuffer)
            {
                sb.Append(" --bitrate ")
                    .Append(profile.Bitrate.A)
                    .Append(":")
                    .Append(profile.Bitrate.B)
                    .Append(":")
                    .Append(profile.Bitrate.H264);
            }

            string[] decoderNames = new string[] { "default", "QSV", "CUVID" };
            if (profile.Mpeg2Decoder != DecoderType.Default)
            {
                sb.Append("  --mpeg2decoder ");
                sb.Append(decoderNames[(int)profile.Mpeg2Decoder]);
            }
            if (profile.H264Deocder != DecoderType.Default)
            {
                sb.Append("  --h264decoder ");
                sb.Append(decoderNames[(int)profile.H264Deocder]);
            }

            if (profile.TwoPass)
            {
                sb.Append(" --2pass");
            }
            if (ignoreNoLogo)
            {
                sb.Append(" --ignore-no-logo");
            }
            if (profile.IgnoreNoDrcsMap)
            {
                sb.Append(" --ignore-no-drcsmap");
            }
            if (profile.NoDelogo)
            {
                sb.Append(" --no-delogo");
            }

            if (logofiles != null)
            {
                foreach (var logo in logofiles)
                {
                    sb.Append(" --logo \"").Append(GetLogoFilePath(logo)).Append("\"");
                }
            }
            if (profile.SystemAviSynthPlugin)
            {
                sb.Append(" --systemavsplugin");
            }

            return sb.ToString();
        }

        private Task NotifyQueueItemUpdate(QueueItem item, QueueDirectory dir)
        {
            return client.OnQueueUpdate(new QueueUpdate() {
                Type = UpdateType.Update,
                DirId = dir.Id,
                Item = item
            });
        }

        private Task AddEncodeLog(string str)
        {
            Util.AddLog(str);
            return client.OnOperationResult(str);
        }

        private void CleanTmpDir()
        {
            foreach (var dir in Directory
                .GetDirectories(appData.setting.ActualWorkPath)
                .Where(s => Path.GetFileName(s).StartsWith("amt")))
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception) { } // 例外は無視
            }
            foreach (var file in Directory
                .GetFiles(appData.setting.ActualWorkPath)
                .Where(s => Path.GetFileName(s).StartsWith("amt")))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception) { } // 例外は無視
            }
        }

        private static void CheckPath(string name, string path)
        {
            if(!string.IsNullOrEmpty(path) && !File.Exists(path))
            {
                throw new InvalidOperationException(name + "パスが無効です: " + path);
            }
        }

        private static void CheckSetting(ProfileSetting profile, Setting setting)
        {
            string workPath = setting.ActualWorkPath;
            if (!File.Exists(setting.AmatsukazePath))
            {
                throw new InvalidOperationException(
                    "AmtasukazeCLIパスが無効です: " + setting.AmatsukazePath);
            }
            if (!Directory.Exists(workPath))
            {
                throw new InvalidOperationException(
                    "一時フォルダパスが無効です: " + workPath);
            }

            // パスが設定されていたらファイル存在チェック
            CheckPath("x264", setting.X264Path);
            CheckPath("x265", setting.X265Path);
            CheckPath("QSVEnc", setting.QSVEncPath);
            CheckPath("NVEnc", setting.NVEncPath);

            CheckPath("L-SMASH Muxer", setting.MuxerPath);
            CheckPath("MP4Box", setting.MP4BoxPath);
            CheckPath("TimelineEditor", setting.TimelineEditorPath);
            CheckPath("MKVMerge", setting.MKVMergePath);
            CheckPath("ChapterExe", setting.ChapterExePath);
            CheckPath("JoinLogoScp", setting.JoinLogoScpPath);
            CheckPath("NicoConvAss", setting.NicoConvASSPath);

            if (profile != null)
            {
                string encoderPath = GetEncoderPath(profile.EncoderType, setting);
                if (string.IsNullOrEmpty(encoderPath))
                {
                    throw new ArgumentException("エンコーダパスが設定されていません");
                }

                if (profile.OutputFormat == FormatType.MP4)
                {
                    if (string.IsNullOrEmpty(setting.MuxerPath))
                    {
                        throw new ArgumentException("L-SMASH Muxerパスが設定されていません");
                    }
                    if (string.IsNullOrEmpty(setting.MP4BoxPath))
                    {
                        throw new ArgumentException("MP4Boxパスが指定されていません");
                    }
                    if (string.IsNullOrEmpty(setting.TimelineEditorPath))
                    {
                        throw new ArgumentException("Timelineeditorパスが設定されていません");
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(setting.MKVMergePath))
                    {
                        throw new ArgumentException("MKVMergeパスが設定されていません");
                    }
                }

                if (!profile.DisableChapter)
                {
                    if (string.IsNullOrEmpty(setting.ChapterExePath))
                    {
                        throw new ArgumentException("ChapterExeパスが設定されていません");
                    }
                    if (string.IsNullOrEmpty(setting.JoinLogoScpPath))
                    {
                        throw new ArgumentException("JoinLogoScpパスが設定されていません");
                    }
                }

                if (profile.EnableNicoJK)
                {
                    if (string.IsNullOrEmpty(setting.NicoConvASSPath))
                    {
                        throw new ArgumentException("NicoConvASSパスが設定されていません");
                    }
                }
            }
        }

        private async Task RemoveAllCompleted()
        {
            // 完了したディレクトリは消す
            foreach (var dir in queue.ToArray())
            {
                if (dir.Items.All(s => !s.IsActive))
                {
                    queue.Remove(dir);

                    await client.OnQueueUpdate(new QueueUpdate() {
                        Type = UpdateType.Remove,
                        DirId = dir.Id
                    });
                }
            }
        }

        private async Task InternalAddQueue(AddQueueDirectory dir)
        {
            List<Task> waitItems = new List<Task>();

            // ユーザ操作でない場合はログを記録する
            bool enableLog = (dir.Mode == ProcMode.AutoBatch);

            ProfileSetting profile = null;
            if(!profiles.TryGetValue(dir.Profile, out profile))
            {
                await NotifyMessage(
                    "プロファイルが見つかりません:" + dir.Profile, enableLog);
                return;
            }

            // プロファイル・設定をチェック
            try
            {
                CheckSetting(profile, appData.setting);
            }
            catch (Exception e)
            {
                waitItems.Add(NotifyMessage(e.Message, enableLog));
                return;
            }

            waitItems.Add(RemoveAllCompleted());

            // 既に追加されているファイルは除外する
            var ignores = queue
                .Where(t => t.DirPath == dir.DirPath);

            // バッチのときは全ファイルが対象だが、バッチじゃなければバッチのみが対象
            if (!dir.IsBatch)
            {
                ignores = ignores.Where(t => t.IsBatch);
            }

            var ignoreSet = new HashSet<string>(
                ignores.SelectMany(t => t.Items).Select(item => item.Path));

            var items = ((dir.Targets != null)
                ? dir.Targets
                : Directory.GetFiles(dir.DirPath)
                    .Where(s =>
                    {
                        string lower = s.ToLower();
                        return lower.EndsWith(".ts") || lower.EndsWith(".m2t");
                    })
                    .Select(f => new AddQueueItem() { Path = f }))
                    .Where(f => !ignoreSet.Contains(f.Path));

            if (dir.Mode != ProcMode.DrcsSearch && dir.DstPath != null && Directory.Exists(dir.DstPath) == false)
            {
                await NotifyMessage(
                    "出力先フォルダが存在しません:" + dir.DstPath, enableLog);
                return;
            }

            string dstPath = dir.DstPath ?? Path.Combine(dir.DirPath, "encoded");
            QueueDirectory target = null;

            // 最後のQueueDirectoryと同じならそこに追加する
            if (queue.Count > 0)
            {
                var last = queue.Last();
                if (last.Items.Count < 100 &&
                    last.DirPath == dir.DirPath &&
                    last.DstPath == dstPath &&
                    last.Mode == dir.Mode &&
                    last.Profile.Name == dir.Profile &&
                    last.Profile.LastUpdate == profile.LastUpdate)
                {
                    target = last;
                }
            }

            if (target == null)
            {
                target = new QueueDirectory()
                {
                    Id = nextDirId++,
                    DirPath = dir.DirPath,
                    Items = new List<QueueItem>(),
                    DstPath = dstPath,
                    Mode = dir.Mode,
                    Profile = ServerSupport.DeepCopy(profile)
                };

                if (dir.IsBatch && profile.DisableHashCheck == false && target.DirPath.StartsWith("\\\\"))
                {
                    var hashpath = target.DirPath + ".hash";
                    if (File.Exists(hashpath) == false)
                    {
                        await NotifyMessage("ハッシュファイルがありません: " + hashpath + "\r\n" +
                            "必要ない場合はハッシュチェックを無効化して再度追加してください", enableLog);
                        return;
                    }
                    try
                    {
                        target.HashList = HashUtil.ReadHashFile(hashpath);
                    }
                    catch (IOException e)
                    {
                        await NotifyMessage("ハッシュファイルの読み込みに失敗: " + e.Message, enableLog);
                        return;
                    }
                }

                queue.Add(target);
                waitItems.Add(client.OnQueueUpdate(new QueueUpdate()
                {
                    Type = UpdateType.Add,
                    Directory = target
                }));
            }

            var map = appData.services.ServiceMap;

            // TSファイル情報を読む
            foreach (var additem in items)
            {
                using (var info = new TsInfo(amtcontext))
                {
                    var fileitems = new List<QueueItem>();
                    var failReason = "";
                    if (await Task.Run(() => info.ReadFile(additem.Path)) == false)
                    {
                        failReason = "TS情報取得に失敗: " + amtcontext.GetError();
                    }
                    else
                    {
                        failReason = "TSファイルに映像が見つかりませんでした";
                        var list = info.GetProgramList();
                        var videopids = new List<int>();
                        int numFiles = 0;
                        for (int i = 0; i < list.Length; ++i)
                        {
                            var prog = list[i];
                            if (prog.HasVideo &&
                                videopids.Contains(prog.VideoPid) == false)
                            {
                                videopids.Add(prog.VideoPid);

                                var serviceName = "不明";
                                var tsTime = DateTime.MinValue;
                                if (info.HasServiceInfo)
                                {
                                    var service = info.GetServiceList().Where(s => s.ServiceId == prog.ServiceId).FirstOrDefault();
                                    if (service.ServiceId != 0)
                                    {
                                        serviceName = service.ServiceName;
                                    }
                                    tsTime = info.GetTime();
                                }

                                var dstname = Path.GetFileName(additem.Path);
                                if (numFiles > 0)
                                {
                                    dstname = Path.GetFileNameWithoutExtension(dstname) + "-マルチ" + numFiles;
                                }

                                var item = new QueueItem()
                                {
                                    Id = nextItemId++,
                                    Path = additem.Path,
                                    Hash = additem.Hash,
                                    DstName = dstname,
                                    ServiceId = prog.ServiceId,
                                    ImageWidth = prog.Width,
                                    ImageHeight = prog.Height,
                                    TsTime = tsTime,
                                    ServiceName = serviceName,
                                    State = QueueState.LogoPending
                                };

                                Debug.Print("解析完了: " + additem.Path);

                                if (item.IsOneSeg)
                                {
                                    item.State = QueueState.PreFailed;
                                    item.FailReason = "映像が小さすぎます(" + prog.Width + "," + prog.Height + ")";
                                }
                                else
                                {
                                    // ロゴファイルを探す
                                    if (dir.Mode != ProcMode.DrcsSearch && map.ContainsKey(item.ServiceId) == false)
                                    {
                                        // 新しいサービスを登録
                                        var newElement = new ServiceSettingElement()
                                        {
                                            ServiceId = item.ServiceId,
                                            ServiceName = item.ServiceName,
                                            LogoSettings = new List<LogoSetting>()
                                        };
                                        map.Add(item.ServiceId, newElement);
                                        serviceListUpdated = true;
                                        waitItems.Add(client.OnServiceSetting(new ServiceSettingUpdate()
                                        {
                                            Type = ServiceSettingUpdateType.Update,
                                            ServiceId = newElement.ServiceId,
                                            Data = newElement
                                        }));
                                    }
                                    UpdateQueueItem(item, target, true);
                                    ++numFiles;
                                }

                                fileitems.Add(item);

                            }
                        }

                    }

                    if (fileitems.Count == 0)
                    {
                        fileitems.Add(new QueueItem()
                        {
                            Id = nextItemId++,
                            Path = additem.Path,
                            Hash = additem.Hash,
                            DstName = "",
                            ServiceId = -1,
                            ImageWidth = -1,
                            ImageHeight = -1,
                            TsTime = DateTime.MinValue,
                            ServiceName = "不明",
                            State = QueueState.PreFailed,
                            FailReason = failReason
                        });
                    }

                    target.Items.AddRange(fileitems);
                    foreach (var item in fileitems)
                    {
                        waitItems.Add(client.OnQueueUpdate(new QueueUpdate()
                        {
                            Type = UpdateType.Add,
                            Item = item,
                            DirId = target.Id
                        }));
                    }
                }
            }

            if (target.Items.Count == 0)
            {
                waitItems.Add(NotifyMessage(
                    "エンコード対象ファイルが見つかりません。パス:" + dir.DirPath, enableLog));

                await Task.WhenAll(waitItems.ToArray());

                return;
            }

            waitItems.Add(RequestFreeSpace());

            await Task.WhenAll(waitItems.ToArray());
        }

        private async Task QueueThread()
        {
            try
            {
                while (await queueQ.OutputAvailableAsync())
                {
                    AddQueueDirectory dir = await queueQ.ReceiveAsync();
                    await InternalAddQueue(dir);
                    await client.OnAddResult(dir.RequestId);
                }
            }
            catch (Exception exception)
            {
                await NotifyMessage("QueueThreadがエラー終了しました: " + exception.Message, true);
            }
        }

        // ペンディング <=> キュー 状態を切り替える
        // ペンディングからキューになったらスケジューリングに追加する
        // 戻り値: 状態が変わった
        private bool UpdateQueueItem(QueueItem item, QueueDirectory dir, bool enqueue)
        {
            if(item.State == QueueState.LogoPending || item.State == QueueState.Queue)
            {
                var prevState = item.State;
                if (dir.Mode == ProcMode.DrcsSearch && item.State == QueueState.LogoPending)
                {
                    item.FailReason = "";
                    item.State = QueueState.Queue;
                    scheduler.QueueItem(new WorkerQueueItem()
                    {
                        Dir = dir,
                        Item = item
                    });
                }
                else
                {
                    var map = appData.services.ServiceMap;
                    if (item.ServiceId == -1)
                    {
                        item.FailReason = "TS情報取得中";
                        item.State = QueueState.LogoPending;
                    }
                    else if (map.ContainsKey(item.ServiceId) == false)
                    {
                        item.FailReason = "このTSに対する設定がありません";
                        item.State = QueueState.LogoPending;
                    }
                    else if (dir.Profile.DisableChapter == false &&
                        map[item.ServiceId].LogoSettings.Any(s => s.CanUse(item.TsTime)) == false)
                    {
                        item.FailReason = "ロゴ設定がありません";
                        item.State = QueueState.LogoPending;
                    }
                    else
                    {
                        // OK
                        if (item.State == QueueState.LogoPending)
                        {
                            item.FailReason = "";
                            item.State = QueueState.Queue;

                            var workerItem = new WorkerQueueItem()
                            {
                                Dir = dir,
                                Item = item
                            };
                            if (enqueue)
                            {
                                scheduler.QueueItem(workerItem);
                            }
                            else
                            {
                                scheduler.StackItem(workerItem);
                            }

                            if(dir.Mode == ProcMode.AutoBatch)
                            {
                                // ユーザ操作以外で追加された場合は、サスペンドを抑止
                                if(preventSuspend == null)
                                {
                                    preventSuspend = new PreventSuspendContext();
                                }
                            }
                        }
                    }
                }
                return prevState != item.State;
            }
            return false;
        }

        private List<Task> UpdateQueueItems()
        {
            List<Task> tasklist = new List<Task>();
            var map = appData.services.ServiceMap;
            foreach (var dir in queue)
            {
                foreach(var item in dir.Items)
                {
                    if (item.State != QueueState.LogoPending && item.State != QueueState.Queue)
                    {
                        continue;
                    }
                    if(UpdateQueueItem(item, dir, false))
                    {
                        tasklist.Add(NotifyQueueItemUpdate(item, dir));
                    }
                }
            }
            return tasklist;
        }

        private bool ReadLogoFile(LogoSetting setting, string filepath)
        {
            try
            {
                var logo = new LogoFile(amtcontext, filepath);

                setting.FileName = Path.GetFileName(filepath);
                setting.LogoName = logo.Name;
                setting.ServiceId = logo.ServiceId;

                return true;
            }
            catch(IOException)
            {
                return false;
            }
        }

        private BitmapFrame LoadImage(string imgpath)
        {
            if(drcsImageCache.ContainsKey(imgpath))
            {
                return drcsImageCache[imgpath];
            }
            try
            {
                var img = BitmapFrame.Create(new MemoryStream(File.ReadAllBytes(imgpath)));
                drcsImageCache.Add(imgpath, img);
                return img;
            }
            catch (Exception) { }

            return null;
        }

        private Dictionary<string, DrcsImage> LoadDrcsImages()
        {
            var ret = new Dictionary<string, DrcsImage>();

            foreach (var imgpath in Directory.GetFiles(GetDRCSDirectoryPath()))
            {
                var filename = Path.GetFileName(imgpath);
                if (filename.Length == 36 && Path.GetExtension(filename).ToLower() == ".bmp")
                {
                    string md5 = filename.Substring(0, 32);
                    ret.Add(md5, new DrcsImage()
                    {
                        MD5 = md5,
                        MapStr = null,
                        Image = LoadImage(imgpath)
                    });
                }
            }

            return ret;
        }

        private Dictionary<string, DrcsImage> LoadDrcsMap()
        {
            Func<char, bool> isHex = c =>
                     (c >= '0' && c <= '9') ||
                     (c >= 'a' && c <= 'f') ||
                     (c >= 'A' && c <= 'F');

            var ret = new Dictionary<string, DrcsImage>();

            try
            {
                foreach (var line in File.ReadAllLines(GetDRCSMapPath()))
                {
                    if (line.Length >= 34 && line.IndexOf("=") == 32)
                    {
                        string md5 = line.Substring(0, 32);
                        string mapStr = line.Substring(33);
                        if (md5.All(isHex))
                        {
                            ret.Add(md5, new DrcsImage() { MD5 = md5, MapStr = mapStr, Image = null });
                        }
                    }
                }
            }
            catch(Exception) {
                // do nothing
            }

            return ret;
        }

        private async Task WatchFileThread()
        {
            try
            {
                var completion = watchFileQ.OutputAvailableAsync();

                var logoDirTime = DateTime.MinValue;
                var logoTime = new Dictionary<string,DateTime>();

                var jlsDirTime = DateTime.MinValue;
                var avsDirTime = DateTime.MinValue;
                var profileDirTime = DateTime.MinValue;

                var drcsDirTime = DateTime.MinValue;
                var drcsTime = DateTime.MinValue;

                // 初期化
                foreach (var service in appData.services.ServiceMap.Values)
                {
                    foreach (var logo in service.LogoSettings)
                    {
                        // 全てのロゴは存在しないところからスタート
                        logo.Exists = (logo.FileName == LogoSetting.NO_LOGO);
                    }
                }

                while (true)
                {
                    string logopath = GetLogoDirectoryPath();
                    if (Directory.Exists(logopath))
                    {
                        var map = appData.services.ServiceMap;

                        var logoDict = new Dictionary<string, LogoSetting>();
                        foreach (var service in map.Values)
                        {
                            foreach (var logo in service.LogoSettings)
                            {
                                if(logo.FileName != LogoSetting.NO_LOGO)
                                {
                                    logoDict.Add(logo.FileName, logo);
                                }
                            }
                        }

                        var updatedServices = new List<int>();

                        var lastModified = Directory.GetLastWriteTime(logopath);
                        if (logoDirTime != lastModified || serviceListUpdated)
                        {
                            logoDirTime = lastModified;

                            // ファイルの個数が変わった or サービスリストが変わった

                            if (serviceListUpdated)
                            {
                                // サービスリストが分かったら再度追加処理
                                serviceListUpdated = false;
                                logoTime.Clear();
                            }

                            var newTime = new Dictionary<string, DateTime>();
                            foreach (var filepath in Directory.GetFiles(logopath)
                                .Where(s => s.EndsWith(".lgd", StringComparison.OrdinalIgnoreCase)))
                            {
                                newTime.Add(filepath, File.GetLastWriteTime(filepath));
                            }

                            foreach (var path in logoTime.Keys.Union(newTime.Keys))
                            {
                                var name = Path.GetFileName(path);
                                if (!newTime.ContainsKey(path))
                                {
                                    // 消えた
                                    if (logoDict.ContainsKey(name))
                                    {
                                        logoDict[name].Exists = false;
                                        updatedServices.Add(logoDict[name].ServiceId);
                                    }
                                }
                                else if (!logoTime.ContainsKey(path))
                                {
                                    // 追加された
                                    if (logoDict.ContainsKey(name))
                                    {
                                        if (logoDict[name].Exists == false)
                                        {
                                            logoDict[name].Exists = true;
                                            ReadLogoFile(logoDict[name], path);
                                            updatedServices.Add(logoDict[name].ServiceId);
                                        }
                                    }
                                    else
                                    {
                                        var setting = new LogoSetting();
                                        ReadLogoFile(setting, path);

                                        if (map.ContainsKey(setting.ServiceId))
                                        {
                                            setting.Exists = true;
                                            setting.Enabled = true;
                                            setting.From = new DateTime(2000, 1, 1);
                                            setting.To = new DateTime(2030, 12, 31);

                                            map[setting.ServiceId].LogoSettings.Add(setting);
                                            updatedServices.Add(setting.ServiceId);
                                        }
                                    }
                                }
                                else if (logoTime[path] != newTime[path])
                                {
                                    // 変更されたファイル
                                    if (logoDict.ContainsKey(name))
                                    {
                                        ReadLogoFile(logoDict[name], path);
                                        updatedServices.Add(logoDict[name].ServiceId);
                                    }
                                }
                            }

                            logoTime = newTime;
                        }
                        else
                        {
                            // ファイルは同じなので、個々のファイルの更新を見る
                            foreach (var key in logoTime.Keys)
                            {
                                var lastMod = File.GetLastWriteTime(key);
                                if (logoTime[key] != lastMod)
                                {
                                    logoTime[key] = lastMod;

                                    var name = Path.GetFileName(key);
                                    if (logoDict.ContainsKey(name))
                                    {
                                        ReadLogoFile(logoDict[name], key);
                                        updatedServices.Add(logoDict[name].ServiceId);
                                    }
                                }
                            }
                        }

                        if(updatedServices.Count > 0)
                        {
                            // 更新をクライアントに通知
                            foreach (var updatedServiceId in updatedServices.Distinct())
                            {
                                await client.OnServiceSetting(new ServiceSettingUpdate() {
                                    Type = ServiceSettingUpdateType.Update,
                                    ServiceId = updatedServiceId,
                                    Data = map[updatedServiceId]
                                });
                            }
                            // キューを再始動
                            await Task.WhenAll(UpdateQueueItems());
                        }
                    }

                    string jlspath = GetJLDirectoryPath();
                    if (Directory.Exists(jlspath))
                    {
                        var lastModified = Directory.GetLastWriteTime(jlspath);
                        if (jlsDirTime != lastModified)
                        {
                            jlsDirTime = lastModified;

                            JlsCommandFiles = Directory.GetFiles(jlspath)
                                .Select(s => Path.GetFileName(s)).ToList();
                            await client.OnCommonData(new CommonData()
                            {
                                JlsCommandFiles = JlsCommandFiles
                            });
                        }
                    }

                    string avspath = GetAvsDirectoryPath();
                    if (Directory.Exists(avspath))
                    {
                        var lastModified = Directory.GetLastWriteTime(avspath);
                        if (avsDirTime != lastModified)
                        {
                            avsDirTime = lastModified;

                            var files = Directory.GetFiles(avspath)
                                .Where(f => f.EndsWith(".avs", StringComparison.OrdinalIgnoreCase))
                                .Select(f => Path.GetFileName(f));

                            MainScriptFiles = files
                                .Where(f => f.StartsWith("メイン_")).ToList();

                            PostScriptFiles = files
                                .Where(f => f.StartsWith("ポスト_")).ToList();

                            await client.OnCommonData(new CommonData()
                            {
                                MainScriptFiles = MainScriptFiles,
                                PostScriptFiles = PostScriptFiles
                            });
                        }
                    }

                    string drcspath = GetDRCSDirectoryPath();
                    string drcsmappath = GetDRCSMapPath();
                    if (Directory.Exists(drcspath) && File.Exists(drcsmappath))
                    {
                        bool needUpdate = false;
                        var lastModified = Directory.GetLastWriteTime(drcspath);
                        if (drcsDirTime != lastModified)
                        {
                            // ファイルが追加された
                            needUpdate = true;
                            drcsDirTime = lastModified;
                        }
                        lastModified = File.GetLastWriteTime(drcsmappath);
                        if (drcsTime != lastModified)
                        {
                            // マッピングが更新された
                            needUpdate = true;
                            drcsTime = lastModified;
                        }
                        if (needUpdate)
                        {
                            var newImageMap = LoadDrcsImages();
                            var newStrMap = LoadDrcsMap();

                            var newDrcsMap = new Dictionary<string, DrcsImage>();
                            foreach(var key in newImageMap.Keys.Union(newStrMap.Keys))
                            {
                                var newItem = new DrcsImage() { MD5 = key };
                                if (newImageMap.ContainsKey(key))
                                {
                                    newItem.Image = newImageMap[key].Image;
                                }
                                if(newStrMap.ContainsKey(key))
                                {
                                    newItem.MapStr = newStrMap[key].MapStr;
                                }
                                newDrcsMap.Add(key, newItem);
                            }

                            // 更新処理
                            var updateImages = new List<DrcsImage>();
                            foreach (var key in newDrcsMap.Keys.Union(drcsMap.Keys))
                            {
                                if (newDrcsMap.ContainsKey(key) == false)
                                {
                                    // 消えた
                                    await client.OnDrcsData(new DrcsImageUpdate() {
                                        Type = DrcsUpdateType.Remove,
                                        Image = drcsMap[key]
                                    });
                                }
                                else if (drcsMap.ContainsKey(key) == false)
                                {
                                    // 追加された
                                    updateImages.Add(newDrcsMap[key]);
                                }
                                else
                                {
                                    var oldItem = drcsMap[key];
                                    var newItem = newDrcsMap[key];
                                    if(oldItem.MapStr != newItem.MapStr || oldItem.Image != newItem.Image)
                                    {
                                        // 変更された
                                        updateImages.Add(newDrcsMap[key]);
                                    }
                                }
                            }

                            if(updateImages.Count > 0)
                            {
                                await client.OnDrcsData(new DrcsImageUpdate() {
                                    Type = DrcsUpdateType.Update,
                                    ImageList = updateImages.Distinct().ToList()
                                });
                            }

                            drcsMap = newDrcsMap;
                        }
                    }

                    string profilepath = GetProfileDirectoryPath();
                    if(!Directory.Exists(profilepath))
                    {
                        Directory.CreateDirectory(profilepath);
                    }
                    {
                        var lastModified = Directory.GetLastWriteTime(profilepath);
                        if (profileDirTime != lastModified)
                        {
                            profileDirTime = lastModified;

                            var newProfiles = Directory.GetFiles(profilepath)
                                .Where(s => s.EndsWith(".profile", StringComparison.OrdinalIgnoreCase))
                                .Select(s => Path.GetFileNameWithoutExtension(s))
                                .ToArray();

                            foreach (var name in newProfiles.Union(profiles.Keys))
                            {
                                var filepath = GetProfilePath(profilepath, name);
                                if (profiles.ContainsKey(name) == false)
                                {
                                    // 追加された
                                    var profile = ReadProfile(filepath);
                                    profile.Name = name;
                                    profile.LastUpdate = File.GetLastWriteTime(filepath);
                                    profiles.Add(profile.Name, profile);
                                    await client.OnProfile(new ProfileUpdate()
                                    {
                                        Type = UpdateType.Add,
                                        Profile = profile
                                    });
                                }
                                else if (newProfiles.Contains(name) == false)
                                {
                                    // 削除された
                                    var profile = profiles[name];
                                    await client.OnProfile(new ProfileUpdate()
                                    {
                                        Type = UpdateType.Remove,
                                        Profile = profile
                                    });
                                }
                                else
                                {
                                    var profile = profiles[name];
                                    var lastUpdate = File.GetLastWriteTime(filepath);
                                    if (profile.LastUpdate != lastUpdate)
                                    {
                                        // 変更された
                                        profile = ReadProfile(filepath);
                                        profile.Name = name;
                                        profile.LastUpdate = lastUpdate;
                                        await client.OnProfile(new ProfileUpdate()
                                        {
                                            Type = UpdateType.Update,
                                            Profile = profile
                                        });
                                    }
                                }
                            }
                            if (profiles.ContainsKey(ServerSupport.GetDefaultProfileName()) == false)
                            {
                                // デフォルトがない場合は追加しておく
                                var profile = ServerSupport.GetDefaultProfile();
                                profile.Name = ServerSupport.GetDefaultProfileName();
                                var filepath = GetProfilePath(profilepath, profile.Name);
                                SaveProfile(filepath, profile);
                                profile.LastUpdate = File.GetLastWriteTime(filepath);
                                profiles.Add(profile.Name, profile);
                                await client.OnProfile(new ProfileUpdate()
                                {
                                    Type = UpdateType.Add,
                                    Profile = profile
                                });
                            }
                        }
                    }

                    if (await Task.WhenAny(completion, Task.Delay(2000)) == completion)
                    {
                        // 終了
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                await NotifyMessage("WatchFileThreadがエラー終了しました: " + exception.Message, true);
            }
        }

        private async Task SaveSettingThread()
        {
            try
            {
                var completion = saveSettingQ.OutputAvailableAsync();

                while (true)
                {
                    if(settingUpdated)
                    {
                        SaveAppData();
                        settingUpdated = false;
                    }

                    if (await Task.WhenAny(completion, Task.Delay(5000)) == completion)
                    {
                        // 終了
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                await NotifyMessage("WatchFileThreadがエラー終了しました: " + exception.Message, true);
            }
        }

        private DiskItem MakeDiskItem(string path)
        {
            ulong available = 0;
            ulong total = 0;
            ulong free = 0;
            Util.GetDiskFreeSpaceEx(path, out available, out total, out free);
            return new DiskItem() { Capacity = (long)total, Free = (long)available, Path = path };
        }

        private void RefrechDiskSpace()
        {
            diskMap = new SortedDictionary<string, DiskItem>();
            if (string.IsNullOrEmpty(appData.setting.AlwaysShowDisk) == false)
            {
                foreach (var path in appData.setting.AlwaysShowDisk.Split(';'))
                {
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }
                    try
                    {
                        var diskPath = Path.GetPathRoot(path);
                        diskMap.Add(diskPath, MakeDiskItem(diskPath));
                    }
                    catch (Exception e)
                    {
                        Util.AddLog("ディスク情報取得失敗: " + e.Message);
                    }
                }
            }
            foreach(var item in queue)
            {
                var diskPath = Path.GetPathRoot(item.DstPath);
                if (diskMap.ContainsKey(diskPath) == false)
                {
                    diskMap.Add(diskPath, MakeDiskItem(diskPath));
                }
            }
            if(string.IsNullOrEmpty(appData.setting.WorkPath) == false) {
                var diskPath = Path.GetPathRoot(appData.setting.WorkPath);
                if (diskMap.ContainsKey(diskPath) == false)
                {
                    diskMap.Add(diskPath, MakeDiskItem(diskPath));
                }
            }
        }

        private static LogoSetting MakeNoLogoSetting(int serviceId)
        {
            return new LogoSetting() {
                FileName = LogoSetting.NO_LOGO,
                LogoName = "ロゴなし",
                ServiceId = serviceId,
                Exists = true,
                Enabled = false,
                From = new DateTime(2000, 1, 1),
                To = new DateTime(2030, 12, 31)
            };
        }

        public Task SetProfile(ProfileUpdate data)
        {
            try
            {
                // 面倒だからAddもUpdateも同じ
                var profilepath = GetProfileDirectoryPath();
                var filepath = GetProfilePath(profilepath, data.Profile.Name);

                if (data.Type == UpdateType.Add || data.Type == UpdateType.Update)
                {
                    CheckSetting(data.Profile, appData.setting);
                    SaveProfile(filepath, data.Profile);
                    data.Profile.LastUpdate = File.GetLastWriteTime(filepath);
                    if (profiles.ContainsKey(data.Profile.Name))
                    {
                        profiles[data.Profile.Name] = data.Profile;
                    }
                    else
                    {
                        profiles.Add(data.Profile.Name, data.Profile);
                    }
                }
                else
                {
                    if(profiles.ContainsKey(data.Profile.Name))
                    {
                        File.Delete(filepath);
                        profiles.Remove(data.Profile.Name);
                    }
                }
                return Task.WhenAll(
                    client.OnProfile(data),
                    AddEncodeLog("プロファイルを更新しました"));
            }
            catch (Exception e)
            {
                return NotifyMessage(e.Message, false);
            }
        }

        public Task SetCommonData(CommonData data)
        {
            try
            {
                if(data.Setting != null)
                {
                    SetDefaultPath(data.Setting);
                    CheckSetting(null, data.Setting);
                    appData.setting = data.Setting;
                    scheduler.SetNumParallel(data.Setting.NumParallel);
                    affinityCreator.NumProcess = data.Setting.NumParallel;
                    settingUpdated = true;
                    return Task.WhenAll(
                        client.OnCommonData(new CommonData() { Setting = appData.setting }),
                        RequestFreeSpace(),
                        AddEncodeLog("設定を更新しました"));
                }
                else if(data.MakeScriptData != null)
                {
                    appData.scriptData = data.MakeScriptData;
                    settingUpdated = true;
                    return client.OnCommonData(new CommonData() {
                        MakeScriptData = data.MakeScriptData
                    });
                }
                return Task.FromResult(0);
            }
            catch(Exception e)
            {
                return NotifyMessage(e.Message, false);
            }
        }

        public Task AddQueue(AddQueueDirectory dir)
        {
            queueQ.Post(dir);
            return Task.FromResult(0);
        }

        public async Task RemoveQueue(int id)
        {
            var target = queue.Find(t => t.Id == id);
            if (target == null)
            {
                await NotifyMessage(
                    "指定されたキューディレクトリが見つかりません", false);
                return;
            }
            queue.Remove(target);
            // 全てキャンセル
            foreach(var item in target.Items)
            {
                item.State = QueueState.Canceled;
            }
            await client.OnQueueUpdate(new QueueUpdate() {
                Type = UpdateType.Remove,
                DirId = target.Id
            });
        }

        public async Task PauseEncode(bool pause)
        {
            EncodePaused = pause;
            Task task = RequestState();
            scheduler.SetPause(pause);
            await task;
        }

        private bool IsRemoteHost(IPHostEntry iphostentry, IPAddress address)
        {
            IPHostEntry other = null;
            try
            {
                other = Dns.GetHostEntry(address);
            }
            catch
            {
                return true;
            }
            foreach (IPAddress addr in other.AddressList)
            {
                if (IPAddress.IsLoopback(addr) || Array.IndexOf(iphostentry.AddressList, addr) != -1)
                {
                    return false;
                }
            }
            return true;
        }

        private byte[] GetMacAddress()
        {
            if (ClientManager == null) return null;

            // リモートのクライアントを見つけて、
            // 接続に使っているNICのMACアドレスを取得する
            IPHostEntry iphostentry = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var client in ClientManager.ClientList)
            {
                if(IsRemoteHost(iphostentry, client.RemoteIP.Address))
                {
                    return ServerSupport.GetMacAddress(client.LocalIP.Address);
                }
            }
            return null;
        }

        public async Task RequestSetting()
        {
            await client.OnCommonData(new CommonData() {
                Setting = appData.setting,
                JlsCommandFiles = JlsCommandFiles,
                MainScriptFiles = MainScriptFiles,
                PostScriptFiles = PostScriptFiles,
                ServerInfo = new ServerInfo()
                {
                    HostName = Dns.GetHostName(),
                    MacAddress = GetMacAddress()
                }
            });
            foreach(var profile in profiles.Values)
            {
                await client.OnProfile(new ProfileUpdate() {
                    Profile = profile,
                    Type = UpdateType.Update
                });
            }
            // プロファイルがないと関連付けできないため、
            // プロファイルを送った後にこれを送る
            await client.OnCommonData(new CommonData()
            {
                MakeScriptData = appData.scriptData,
            });
        }

        public Task RequestQueue()
        {
            QueueData data = new QueueData()
            {
                Items = queue
            };
            return client.OnQueueData(data);
        }

        public Task RequestLog()
        {
            return client.OnLogData(log);
        }

        public Task RequestConsole()
        {
            return Task.WhenAll(scheduler.Workers.Cast<TranscodeWorker>().Select(w =>
                client.OnConsole(new ConsoleData() {
                    index = w.id,
                    text = w.consoleText.TextLines as List<string>
                })));
        }

        public Task RequestLogFile(LogItem item)
        {
            return client.OnLogFile(ReadLogFIle(item.EncodeStartDate));
        }

        public Task RequestState()
        {
            var state = new State() {
                Pause = encodePaused,
                Running = nowEncoding
            };
            return client.OnCommonData(new CommonData()
            {
                State = state
            });
        }

        public Task RequestFreeSpace()
        {
            RefrechDiskSpace();
            return client.OnCommonData(new CommonData() {
                Disks = diskMap.Values.ToList()
            });
        }

        public Task RequestDrcsImages()
        {
            return client.OnDrcsData(new DrcsImageUpdate() {
                Type = DrcsUpdateType.Update,
                ImageList = drcsMap.Values.ToList()
            });
        }

        public async Task SetServiceSetting(ServiceSettingUpdate update)
        {
            var serviceMap = appData.services.ServiceMap;
            if(serviceMap.ContainsKey(update.ServiceId))
            {
                if (update.Type == ServiceSettingUpdateType.Update)
                {
                    var old = serviceMap[update.ServiceId];
                    if (old.LogoSettings.Count == update.Data.LogoSettings.Count)
                    {
                        // ロゴのExitsフラグだけはこちらのデータを継承させる
                        for (int i = 0; i < old.LogoSettings.Count; ++i)
                        {
                            update.Data.LogoSettings[i].Exists = old.LogoSettings[i].Exists;
                        }
                        serviceMap[update.ServiceId] = update.Data;
                        await Task.WhenAll(UpdateQueueItems());
                    }
                }
                else if (update.Type == ServiceSettingUpdateType.AddNoLogo)
                {
                    var service = serviceMap[update.ServiceId];
                    service.LogoSettings.Add(MakeNoLogoSetting(update.ServiceId));
                    update.Type = ServiceSettingUpdateType.Update;
                    update.Data = service;
                }
                else if (update.Type == ServiceSettingUpdateType.Remove)
                {
                    serviceMap.Remove(update.ServiceId);
                    update.Data = null;
                }
                else if (update.Type == ServiceSettingUpdateType.RemoveLogo)
                {
                    var service = serviceMap[update.ServiceId];
                    service.LogoSettings.RemoveAt(update.RemoveLogoIndex);
                    update.Type = ServiceSettingUpdateType.Update;
                    update.Data = service;
                }
                settingUpdated = true;
            }
            await client.OnServiceSetting(update);
        }

        public async Task RequestServiceSetting()
        {
            var serviceMap = appData.services.ServiceMap;
            await client.OnServiceSetting(new ServiceSettingUpdate()
            {
                Type = ServiceSettingUpdateType.Clear
            });
            foreach (var service in serviceMap.Values)
            {
                await client.OnServiceSetting(new ServiceSettingUpdate() {
                    Type = ServiceSettingUpdateType.Update,
                    ServiceId = service.ServiceId,
                    Data = service
                });
            }
        }

        private AMTContext amtcontext = new AMTContext();
        public Task RequestLogoData(string fileName)
        {
            if(fileName == LogoSetting.NO_LOGO)
            {
                return NotifyMessage("[RequestLogoData] 不正な操作です", false);
            }
            string logopath = GetLogoFilePath(fileName);
            try
            {
                var logofile = new LogoFile(amtcontext, logopath);
                return client.OnLogoData(new LogoData() {
                    FileName = fileName,
                    ServiceId = logofile.ServiceId,
                    ImageWith = logofile.ImageWidth,
                    ImageHeight = logofile.ImageHeight,
                    Image = logofile.GetImage(0)
                });
            }
            catch(IOException exception)
            {
                return NotifyMessage(
                    "ロゴファイルを開けません。パス:" + logopath + "メッセージ: " + exception.Message, false);
            }
        }

        private Task ReEnqueueItem(QueueItem item, QueueDirectory dir)
        {
            item.State = QueueState.LogoPending;
            UpdateQueueItem(item, dir, false);
            return NotifyQueueItemUpdate(item, dir);
        }

        private static IEnumerable<string> GetFileExtentions(string tsext, bool withEDCB)
        {
            if(tsext != null)
            {
                yield return tsext;
            }
            if(withEDCB)
            {
                yield return ".ts.err";
                yield return ".ts.program.txt";
            }
        }

        private static void MoveTSFile(string file, string dstDir, bool withEDCB)
        {
            string body = Path.GetFileNameWithoutExtension(file);
            string tsext = Path.GetExtension(file);
            string srcDir = Path.GetDirectoryName(file);
            foreach (var ext in GetFileExtentions(tsext, withEDCB))
            {
                string srcPath = srcDir + "\\" + body + ext;
                string dstPath = dstDir + "\\" + body + ext;
                if (File.Exists(srcPath))
                {
                    File.Move(srcPath, dstPath);
                }
            }
        }

        public Task ChangeItem(ChangeItemData data)
        {
            var all = queue.SelectMany(d => d.Items);
            var target = all.FirstOrDefault(s => s.Id == data.ItemId);
            if(target != null)
            {
                var dir = queue.First(d => d.Items.Contains(target));

                if(data.ChangeType == ChangeItemType.Retry)
                {
                    if(target.State == QueueState.Complete ||
                        target.State == QueueState.Failed ||
                        target.State == QueueState.Canceled)
                    {
                        // バッチモードでfailedフォルダに移動されていたら戻す
                        if (target.State == QueueState.Failed && dir.IsBatch)
                        {
                            if (all.Where(s => s.DstName == target.DstName).Any(s => s.IsActive) == false)
                            {
                                var failedPath = dir.Failed + "\\" + Path.GetFileName(target.FileName);
                                if (File.Exists(failedPath))
                                {
                                    // EDCB関連ファイルも移動したかどうかは分からないが、あれば戻す
                                    MoveTSFile(failedPath, dir.DirPath, true);
                                }
                            }
                        }
                        return ReEnqueueItem(target, dir);
                    }
                }
                else if(data.ChangeType == ChangeItemType.Cancel)
                {
                    if(target.IsActive)
                    {
                        target.State = QueueState.Canceled;
                        return NotifyQueueItemUpdate(target, dir);
                    }
                }

            }
            return Task.FromResult(0);
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);

        public async Task AddDrcsMap(DrcsImage recvitem)
        {
            if (drcsMap.ContainsKey(recvitem.MD5))
            {
                var item = drcsMap[recvitem.MD5];

                if (item.MapStr != recvitem.MapStr)
                {
                    var filepath = GetDRCSMapPath();
                    var updateType = DrcsUpdateType.Update;

                    // データ更新
                    if (item.MapStr == null)
                    {
                        // 既存のマッピングにないので追加
                        item.MapStr = recvitem.MapStr;
                        try
                        {
                            using (var sw = File.AppendText(filepath))
                            {
                                sw.WriteLine(item.MD5 + "=" + recvitem.MapStr);
                            }
                        }
                        catch (Exception e) {
                            await NotifyMessage(
                                "DRCSマッピングファイル書き込みに失敗: " + e.Message, false);
                        }
                    }
                    else
                    {
                        // 既にマッピングにある
                        item.MapStr = recvitem.MapStr;
                        if(item.MapStr == null)
                        {
                            // 削除
                            drcsMap.Remove(recvitem.MD5);
                            updateType = DrcsUpdateType.Remove;
                            try
                            {
                                File.Delete(GetDRCSImagePath(recvitem.MD5));
                            }
                            catch(Exception e)
                            {
                                await NotifyMessage(
                                    "DRCS画像ファイル削除に失敗: " + e.Message, false);
                            }
                        }

                        // まず、一時ファイルに書き込む
                        var tmppath = filepath + ".tmp";
                        // BOMありUTF-8
                        try
                        {
                            using (var sw = new StreamWriter(File.OpenWrite(tmppath), Encoding.UTF8))
                            {
                                foreach (var s in drcsMap.Values)
                                {
                                    sw.WriteLine(s.MD5 + "=" + s.MapStr);
                                }
                            }
                        }
                        catch (Exception e)
                        {
                            await client.OnOperationResult(
                                "DRCSマッピングファイル書き込みに失敗: " + e.Message);
                        }
                        // ファイル置き換え
                        MoveFileEx(tmppath, filepath, 11);
                    }

                    await client.OnDrcsData(new DrcsImageUpdate() {
                        Type = updateType,
                        Image = item
                    });
                }
            }
        }

        public Task EndServer()
        {
            finishRequested?.Invoke();
            return Task.FromResult(0);
        }
    }
}