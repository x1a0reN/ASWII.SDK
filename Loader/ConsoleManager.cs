using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
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
    private static readonly bool AutoDumpGameAssembly = false;
    // One-shot maintenance switch. Keep disabled for normal play: batch reflection/Cecil
    // work has no place in the game's long-lived 32-bit process.
    private static readonly bool AutoDumpProtectedAssemblies = false;
    private static readonly bool TelemetryOnlyMode = false;

    
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
        int pid = -1;
        try { pid = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }
        FileLogger.Init(Path.Combine(logDir, "ASW_App.pid" + pid + ".log"), rotate: true);
        FileLogger.Log("MARK", "Start() ENTER");

        // 捕获 Unity 日志
        Application.RegisterLogCallback(new Application.LogCallback(this.HandleLog));

        // 注入时游戏已经联网，只预装本地保护和只读断线诊断。
        // 功能补丁在授权成功后安装，协议报文 Hook 保持禁用。
        HarmonyLoader.InstallProtection();
        FileLogger.Log("MARK", "Runtime protection installed before auth.");

        if (AutoDumpProtectedAssemblies)
        {
            StartProtectedAssemblyBatchDump();
            FileLogger.Log("MARK", "Protected managed assembly batch dump armed.");
        }
        else if (AutoDumpGameAssembly)
        {
            StartStructuredDump();
            StartCoroutine(DeobfRepackRoutine());
            FileLogger.Log("MARK", "Auto game assembly dump armed.");
        }

        // 网络验证通过后，再启用具体功能和菜单。
        if (VeriGateAuthManager.Instance == null)
        {
            var go = new GameObject("VeriGateAuthManager");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<VeriGateAuthManager>();
        }

        var mgr = VeriGateAuthManager.Instance;
        if (mgr == null)
        {
            FileLogger.Log("AUTH", "VeriGateAuthManager 初始化失败。");
            return;
        }

        mgr.RunAutoLogin((ok, err) =>
        {
            if (!ok)
            {
                FileLogger.Log("AUTH", "自动登录失败：" + (string.IsNullOrEmpty(err) ? "未知错误" : err));
                return;
            }

            FileLogger.Log("AUTH", "VeriGate 登录成功：SessionID=" + mgr.SessionID + " DeviceID=" + mgr.DeviceID);
            if (!string.IsNullOrEmpty(mgr.StaticExpiredText))
            {
                FileLogger.Log("AUTH", "卡密到期时间：" + mgr.StaticExpiredText);
            }
            else
            {
                FileLogger.Log("AUTH", "卡密到期时间：未获取");
            }

            HarmonyLoader.InstallAuthorized(TelemetryOnlyMode);
            BootCheatMain();
            FileLogger.Log("MARK", "Auth passed. Authorized feature patches and CheatMain started.");
        });
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

    private void StartProtectedAssemblyBatchDump()
    {
        try
        {
            // Static disk inspection identified these game-provided images as encrypted.
            // The order puts the SurvivalBot dependencies first so they survive a later dump failure.
            StructuredILDump.TARGET_ASSEMBLY_NAME = "Assembly-CSharp";
            StructuredILDump.WAIT_SECONDS = 180;
            StructuredILDump.EXTRA_DELAY_MS = (int)(RuntimeDumpDelaySeconds * 1000f);
            StructuredILDump.DUMP_PROTECTED_ASSEMBLY_BATCH = true;
            StructuredILDump.BATCH_TARGET_ASSEMBLY_NAMES = new string[]
            {
                "RAIN",
                "RAINMetaform",
                "Assembly-CSharp",
                "Assembly-CSharp-firstpass",
                "Assembly-UnityScript-firstpass",
                "Pathfinding.ClipperLib",
                "Pathfinding.Ionic.Zip.Reduced",
                "Pathfinding.JsonFx",
                "Pathfinding.Poly2Tri",
                "LitJson",
                "LZ4",
                "Boo.Lang",
                "Mono.Posix",
                "Mono.Security",
                "System.Configuration",
                "System.Core",
                "System",
                "System.Security",
                "System.Xml",
                "UnityEngine",
                "UnityEngine.UI",
                "mscorlib"
            };
            // Runtime-readable MZ images are sufficient for framework assemblies. Only rebuild
            // the protected game assemblies whose method bodies are required for analysis.
            StructuredILDump.REPACK_ASSEMBLY_NAMES = new string[]
            {
                "RAIN",
                "RAINMetaform",
                "Assembly-CSharp",
                "Assembly-CSharp-firstpass",
                "Assembly-UnityScript-firstpass"
            };
            StructuredILDump.Init();
            FileLogger.Log("MARK", "Protected assembly batch configured. targetCount=" +
                StructuredILDump.BATCH_TARGET_ASSEMBLY_NAMES.Length);
        }
        catch (Exception ex)
        {
            FileLogger.Log("ERROR", "Protected assembly batch init failed: " + ex);
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
        string dataDir = Application.dataPath;
        string baseDir = Path.GetDirectoryName(dataDir);
        string p1 = Path.Combine(Path.Combine(baseDir, "Managed"), "Assembly-CSharp.dll");
        if (File.Exists(p1)) return p1;

        string p2 = Path.Combine(dataDir, "Managed/Assembly-CSharp.dll");
        if (File.Exists(p2)) return p2;

        try
        {
            string loc = typeof(ConsoleManager).Assembly.Location;
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                return loc;
        }
        catch { }

        return null;
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
		System.Text.StringBuilder stringBuilder = new System.Text.StringBuilder();
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
