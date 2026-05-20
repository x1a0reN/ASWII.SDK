using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using ASWDEBUG.Patch;
using ASWDEBUG.UI;
using ASWDEBUG.Logger;
using ASWDEBUG.Global;
using ASWDEBUG.Main;
using ASWDEBUG.Verify;


public class ConsoleManager : MonoBehaviour
{
    private const float RuntimeDumpDelaySeconds = 12f;
    private static readonly bool TelemetryOnlyMode = false;

    
    // 固定单码文件（注意 @ 避免 \x1a 转义）
    private const string SingleCodePath = @"C:\x1a0reN\config.txt";

    public static ConsoleManager Instance
	{
		get
		{
			if (ConsoleManager.instance == null)
			{
				ConsoleManager.instance = new GameObject("ConsoleManager").AddComponent<ConsoleManager>();
			}
			return ConsoleManager.instance;
		}
	}

	private void ProcessExceptionReport(string condition, string stacktrace, LogType type)
	{
		if (type == LogType.Error || type == LogType.Exception)
		{
			this.LogError(condition + stacktrace);
		}
		if (this.onError != null)
		{
			this.onError(condition, stacktrace, type);
		}
	}

    private void Awake() {

        if (ConsoleManager.instance != null && ConsoleManager.instance != this)
        {
            Destroy(this.gameObject);
            return;
        }
        ConsoleManager.instance = this;

        DontDestroyOnLoad(this.gameObject);
        this.gameObject.hideFlags = HideFlags.HideAndDontSave;

        this.windowPos = new Rect(0f, 0f, (float)(Screen.width / 2), (float)(Screen.height / 2));
        this.white = this.yellow = this.red = true;
    }

    private void Start()
    {
        // 日志初始化
        string logDir = Path.Combine(Application.persistentDataPath, "Logs");
        try { Directory.CreateDirectory(logDir); } catch { }
        FileLogger.Init(Path.Combine(logDir, "ASW_App.log"), rotate: true);
        FileLogger.Log("MARK", "Start() ENTER");

        //捕获 Unity 日志
        Application.RegisterLogCallback(new Application.LogCallback(this.HandleLog));
        // [确认] 和之前不断连的版本完全一致：TelemetryOnly
        HarmonyLoader.Install(true);
        BootCheatMain();
        //StartCoroutine(DeobfRepackRoutine());
        //StartStructuredDump();
        FileLogger.Log("MARK", "TelemetryOnly mode (confirmed safe).");

        //return;
        //确保 EyAuthManager 存在
        //if (EyAuthManager.Instance == null)
        //{
        //    var go = new GameObject("EyAuthManager");
        //    go.hideFlags = HideFlags.HideAndDontSave;
        //    DontDestroyOnLoad(go);
        //    go.AddComponent<EyAuthManager>();
        //}

        //var mgr = EyAuthManager.Instance;

        // 1) 先检测是否最新版（42252）
        //if (!mgr.CheckAppVersionEncrypted(mgr.AppVersion, out var verHead))
        //{
        //    FileLogger.Log("AUTH", "版本检测失败或非最新版，服务器返回：" + (verHead ?? "<null>"));
        //    try { Application.Quit(); } catch { }
        //    try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
        //    return;
        //}
        //FileLogger.Log("AUTH", "版本检测通过：已是最新版。");

        // 2) 登录（内部开线程，失败会自行退出）
        //mgr.RunAutoLogin((ok, err) =>
        //{
        //    if (!ok)
        //    {
        //        FileLogger.Log("AUTH", "自动登录失败：" + (string.IsNullOrEmpty(err) ? "未知错误" : err));
        //        // RunAutoLogin 内已负责退出
        //        return;
        //    }

        //    FileLogger.Log("AUTH", "登录成功：SingleCode=" + mgr.SingleCode + " Token=" + mgr.Token);

        //    // 3) 登录成功 → 获取到期时间（42248）
        //    if (mgr.GetExpiredEncrypted(mgr.SingleCode, out var expired))
        //    {
        //        FileLogger.Log("AUTH", "到期时间：" + expired);
        //        // 安装补丁
        //        HarmonyLoader.Install();
        //        // 启动作弊
        //        BootCheatMain();
        //    }
        //    else
        //    {
        //        FileLogger.Log("AUTH", "获取到期时间失败");
        //    }

        //    // 业务入口已在 EyAuthManager 内部（IAuthorizedEntry）自动启动
        //});
    }


    private void BootCheatMain()
    {
        try
        {
            if (CheatMain.Instance != null)
            {
                //FileLogger.Log("BOOT", "CheatMain already exists.");
                return;
            }

            GameObject host = new GameObject("CheatMain");
            host.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(host);
            host.AddComponent<CheatMain>();
            //FileLogger.Log("BOOT", "CheatMain created on dedicated host.");
        }
        catch (Exception e)
        {
            //FileLogger.Log("BOOT", "BootCheatMain failed: " + e);
        }
    }

    private void HandleLog(string condition, string stacktrace, LogType type)
    {
        if (type == LogType.Exception || type == LogType.Error)
        {
            FileLogger.LogException(condition, stacktrace);
            if (this.onError != null) this.onError(condition, stacktrace, type);
        }
        else
        {
            FileLogger.Log(type.ToString().ToUpper(), condition + (string.IsNullOrEmpty(stacktrace) ? "" : ("\n" + stacktrace)));
        }
    }

    private IEnumerator DeobfRepackRoutine()
    {
        FileLogger.Log("MARK", "Deobf coroutine ENTER");

        // 等待游戏自身的热更/混淆器把最终 IL 写完；按需调长
        yield return new WaitForSeconds(RuntimeDumpDelaySeconds);
        FileLogger.Log("MARK", "after wait");

        // 在主线程上收集 Unity 相关信息与引用（后台线程禁止碰 Unity API）
        Assembly liveAsm = FindAssemblyBySimpleName("Assembly-CSharp");
        string templatePath = TryGetTemplatePath();
        string outPath = Path.Combine(Application.persistentDataPath, "Assembly-CSharp.deobf.dll");

        FileLogger.Log("MARK", "liveAsm=" + (liveAsm != null));
        FileLogger.Log("MARK", "template=" + templatePath);
        FileLogger.Log("MARK", "outPath=" + outPath);

        if (liveAsm == null)
        {
            FileLogger.Log("ERROR", "Assembly-CSharp 未加载，取消重建。");
            yield break;
        }
        if (string.IsNullOrEmpty(templatePath) || !File.Exists(templatePath))
        {
            FileLogger.Log("ERROR", "找不到模板 DLL: " + (templatePath ?? "<null>"));
            yield break;
        }

        // 捕获到局部，传入后台线程
        var capAsm = liveAsm;
        var capTpl = templatePath;
        var capOut = outPath;

        // 启动后台线程执行重建
        FileLogger.Log("MARK", "spawn worker thread");
        var th = new System.Threading.Thread(() =>
        {
            try
            {
                FileLogger.Log("MARK", "worker ENTER");
                CecilRepacker.RepackFromLiveIL(capAsm, capTpl, capOut);
                FileLogger.Log("INFO", "REPACK DONE (thread): " + capOut);
            }
            catch (Exception ex)
            {
                FileLogger.Log("ERROR", "REPACK FAIL (thread): " + ex);
            }
        });
        th.IsBackground = true;
        th.Name = "CecilRepackWorker";
        th.Start();

        // 协程到此结束；若你想等待线程结束，可在此轮询 th.IsAlive（一般没必要）
        yield break;
    }

    private void StartStructuredDump()
    {
        try
        {
            StructuredILDump.TARGET_ASSEMBLY_NAME = "Assembly-CSharp";
            StructuredILDump.WAIT_SECONDS = 180;
            StructuredILDump.EXTRA_DELAY_MS = (int)(RuntimeDumpDelaySeconds * 1000f);
            StructuredILDump.Init();
            FileLogger.Log("MARK", "StructuredILDump.Init() armed. extraDelayMs=" + StructuredILDump.EXTRA_DELAY_MS);
        }
        catch (Exception ex)
        {
            FileLogger.Log("ERROR", "StructuredILDump.Init() failed: " + ex);
        }
    }

    private Assembly FindAssemblyBySimpleName(string name)
    {
        var asms = System.AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < asms.Length; i++)
        {
            try { if (asms[i].GetName().Name == name) return asms[i]; }
            catch { }
        }
        return null;
    }

    private string TryGetTemplatePath()
    {
        // 运行时常见路径：<Game>_Data/Managed/Assembly-CSharp.dll
        string dataDir = Application.dataPath;
        string baseDir = Path.GetDirectoryName(dataDir); // 去掉 *_Data
                                                         // 1) <exe目录>/<Game>_Data/Managed/Assembly-CSharp.dll
        string p1 = Path.Combine(Path.Combine(baseDir, "Managed"), "Assembly-CSharp.dll");
        if (File.Exists(p1)) return p1;

        // 2) <exe目录>/<Game>_Data/Managed/Assembly-CSharp.dll （某些旧版 dataPath 就是 *_Data）
        string p2 = Path.Combine(dataDir, "Managed/Assembly-CSharp.dll");
        if (File.Exists(p2)) return p2;

        // 3) 兜底：用自身程序集位置反推（某些版本能拿到）
        try
        {
            string loc = typeof(ConsoleManager).Assembly.Location;
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                return loc;
        }
        catch { }

        return null;
    }

    public void setEnable(bool value)
	{
		this.debugEnable = value;
	}

	public void addMessage(string message, int type)
	{
		if (this.messageList.Count > 0 && this.messageList[this.messageList.Count - 1].messageType == type)
		{
			ConsoleManager.ConsoleMessage consoleMessage = this.messageList[this.messageList.Count - 1];
			consoleMessage.str = consoleMessage.str + "\n" + message;
			return;
		}
		if (this.messageList.Count > 50)
		{
			this.messageList.RemoveRange(0, this.messageList.Count - 50);
		}
		this.messageList.Add(new ConsoleManager.ConsoleMessage(message, type));
	}

	public void WriteLogToFile(string path)
	{
		StringBuilder stringBuilder = new StringBuilder();
		foreach (ConsoleManager.ConsoleMessage consoleMessage in this.messageList)
		{
			stringBuilder.AppendLine(consoleMessage.str);
		}
		File.AppendAllText(path, stringBuilder.ToString());
	}

	private void Update()
	{
		//if (Input.GetKeyDown(KeyCode.Delete))
		//{
		//	this.show = !this.show;
		//}
	}

    private string[] inputFields = new string[5];

    public void MyLog(object obj)
    {
        if (Application.isEditor)
        {
            Debug.Log(obj);
            return;
        }
        if (obj == null)
        {
            this.addMessage("null", 0);
            return;
        }
        this.addMessage(obj.ToString(), 0);

        FileLogger.Log("INFO", obj == null ? "null" : obj.ToString());
    }

    public void Log(object obj)
    {
        return;
        if (Application.isEditor)
		{
			Debug.Log(obj);
			return;
		}
		if (obj == null)
		{
			this.addMessage("null", 0);
			return;
		}
		this.addMessage(obj.ToString(), 0);
	}

	public void LogWarning(object obj)
    {
        return;
        if (Application.isEditor)
		{
			Debug.LogWarning(obj);
			return;
		}
		if (obj == null)
		{
			this.addMessage("null", 1);
			return;
		}
		this.addMessage(obj.ToString(), 1);
	}

	private IEnumerator SendDebug(string debugstr)
	{
		WWW wWW = new WWW(debugstr);
		yield return wWW;
		yield break;
	}

	private string GetDebugStr(string name, string id, string content)
	{
		DateTime.Now.ToLongTimeString();
		return string.Concat(new string[]
		{
			"http://debug.asw.61.com/?<log><name>",
			name,
			"</name><id>",
			id,
			"+</id><content>",
			content,
			"</content></log>"
		});
	}

	public void LogError(object obj)
    {
        return;
        if (Application.isEditor)
		{
			Debug.LogError(obj);
			return;
		}
		if (obj == null)
		{
			this.addMessage("null", 2);
			return;
		}
		this.addMessage(obj.ToString(), 2);
	}

	private void Output(string text)
	{
		return;
		if (Application.isEditor)
		{
			Debug.Log(text);
			return;
		}
		this.addMessage(text, 0);
	}

	private static ConsoleManager instance;

	private Rect windowPos;

	private Vector2 scrollPos;

	private bool white;

	private bool yellow;

	private bool red;

	private List<ConsoleManager.ConsoleMessage> messageList = new List<ConsoleManager.ConsoleMessage>();

	private bool show;

	private string cmd = "";

	private bool debugEnable = true;

	public ConsoleManager.SendCMD onsendcmd;

	public Action<string, string, LogType> onError;

	private class ConsoleMessage
	{

		public ConsoleMessage(string message, int type)
		{
			this.str = message;
			this.messageType = type;
			switch (type)
			{
			case 0:
				this.color = Color.white;
				return;
			case 1:
				this.color = Color.yellow;
				return;
			case 2:
				this.color = Color.red;
				return;
			case 3:
				this.color = new Color(0f, 1f, 1f);
				return;
			default:
				return;
			}
		}


		public int messageType;

		public string str;

		public Color color;
	}

	public delegate string SendCMD(string cmd);
}
