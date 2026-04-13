using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;                    // 保序需要
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using UnityEngine;

// BouncyCastle
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.OpenSsl;
using ASWDEBUG.Logger;

namespace ASWDEBUG.Verify
{
    /// <summary>业务入口接口：登录成功后会调用它。</summary>
    public interface IAuthorizedEntry
    {
        void OnAuthorizedStart();
    }

    /// <summary>w.eydata.net WebAPI 验证（Unity/.NET 3.5 版）。</summary>
    public sealed class EyAuthManager : MonoBehaviour
    {
        public static EyAuthManager Instance { get; private set; }

        // ===== 可按需修改 =====
        public string LoginUrl = "https://vip1.eydata.net/d55b4ef9e04f872e";   // 42246
        public string LogoutUrl = "https://vip1.eydata.net/e56b04848543f7d3";   // 42250
        public string VersionUrl = "https://vip1.eydata.net/5d2016b2a08f8b9d";   // 42252
        public string ExpiredUrl = "https://vip1.eydata.net/71849fefc29477d9";   // 42248
        public string UserStatusUrl = "https://vip1.eydata.net/a7a948d7e30f614a"; // 42247
        public string VariableUrl = "https://vip1.eydata.net/938f720e9fc99ae3";   // 42251

        // ini 路径：优先绝对路径，其次工作目录
        public string AbsIniPath = @"C:\x1a0reN\config.ini";
        public string RelIniPath = "config.ini";

        // 版本/MAC 采集（按服务端约定改）
        public string AppVersion = "1.1";

        // 只读状态
        public volatile bool LoggedIn;
        public string LastError;
        public string Token;            // 登录成功返回的 32 位 code
        public string SingleCode;       // 登录使用的 singlecode（配置里拿）
        public string StaticExpiredText;       // 到期时间

        private readonly Queue<Action> _mainQ = new Queue<Action>();
        private readonly object _lock = new object();

        // ===== 心跳（异步）=====
        private Coroutine _hbCo;
        private volatile bool _hbInFlight;

        // ====== 42246（登录）密钥 ======
        private const string ServerPublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCz7nOhFxfGNNaRW47oyT0E61Tg
kRIA0dNOwsb1YIiF900bsV/4GEW4GhvIAaAEvAyDTJhap8Rp7/BLhUezxK5MazKv
HCPvKqjesdLGtBtu3KCL13NgGQ7KsVCh+Ac5R7UnB2FWDEmjosre3lVA+h/YmeAW
Ay7F+82yc6Vxqg/U7wIDAQAB
-----END PUBLIC KEY-----";

        private const string ServerPrivateKeyPem = @"-----BEGIN RSA PRIVATE KEY-----
MIICXQIBAAKBgQCT/CZY1f7iYMYxvi57fPVfgmn8/Ic6iYst5SD1LB9pewsdbcM6
SVKTYOoggH+SJ5QdmtceIlT28fxMR3o706sisSAI86RrnqFmzilrwUEa7TLIv5nS
+su8q/xJJajTQmWueps/taRSdBh7iJRadZ7ilWWRPpcPB3YJGw6OpggMswIDAQAB
AoGAKrMQ5YUpvGwsA+JaSyttKZfZuTOsWUmirSV18wg+MBNey6kGMeVCPGA0bhhl
tuhQppItC/bgCTkdkWz2ahjTQghHyeXxngMB1+azNAT7xhuiFp7oTiaG01YnkumJ
AL1i7qXKABhysqsVKetEHmO4tlSF2uCaaVImGYaJLJAz5vECQQDaJVUJoMCoaLTD
ozUEjBkWGCqbvggGC+rQtxsy42Fb1SyJdl83EQgx0FRb2gcR+k67boWw0zBfG+IZ
xZIG1DxdAkEAraoRtStVjo0GE5RkRpsinBqBiORqZKO+guIRehG9hIBDAip0wSTA
lRTUxxzN74cXbBbyxfvWCj0F/cF8SrhcTwJBANKSwtl+YTqvh/5pZt4y1mxre4XH
FDux+ULr3cdrkilxR4KRzyt6t2xOa4AWoEiMVL+82jRsR/8nDURPYLxS1skCQERX
vHY+ooHh77U+3aOHo7wpFjcIJPKMGgop61TNrHZ7f2NXz/C+hOdmdkIRjN2pnUcV
VN8jN116HGR7g21oVjMCQQC4exj86Fs0HfKQ7pjWwRSLEpi5fzq+CuOIJFClEXFQ
2OLw4jnXHBI//c5FSkYvuhYpt98HBIckK1UURX9LpUgz
-----END RSA PRIVATE KEY-----";

        // ====== 42250（登出）密钥 ======
        private const string LogoutPublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCDwBIKF6nhZX3a0mfVjqpf34HO
0dnzuyRnmOVzVuKYgnlzuEmNNQP7ad41PLPE1BaQd7gnVcgcQ6ZA3RXrE0fKqnvv
arj/FFSeGBNnQuS9bvMpBoTedF3kTtnllG+nvm39xuay/2CY15VDh1Rj5YjHhcBp
sC9x3Z2iRVtjNgxZ5wIDAQAB
-----END PUBLIC KEY-----";

        private const string LogoutPrivateKeyPem = @"-----BEGIN RSA PRIVATE KEY-----
MIICXQIBAAKBgQCJeYx4kezU/oln4y0QTriu+4jAwAUpkKTGf/T+KbFRtHUwmnsZ
/lmEkWYyYuh3c72gKMi9A1SjNA+dMMgVoKGo1XPDUnu9XJ6CCr23TPi133LrX4Yj
BBfhmBs7OPQrSBHPjd+5dlGiWmPDaIb/5zX6n7CDCz8Dtcu4LF4LQpRSCwIDAQAB
AoGAA+FUa/bokqguzaniVMOI2YYuCnL4fP9YlJjG17BzGcNv7Zo645eQjMsbRvch
YGRoz1Npf9pgPwS82yaDwvWT3886CmONJix5GZ9w5Poz5VJqwI6Gs8IxlFZbrz9K
psnZEg5EtQuN40s/LDm5zj2KAVwvzPfdG/FmWiA/gjGvBRkCQQDD0Q6zn9twPb0q
42J3gvlieUFgP07F5pjchqN9ZTn6KmKK3M1ACiKQwdhZiXA9ROZhCa5l7c0el+UB
QPaYPZnzAkEAs7ohEsA9y/I9+W0Pi0Y0AB0ONT4ucy3UQ0ij7IvKOK4Ey1gyG3VO
XvPOZaBnA/GEd2Y/X1HbsOYMJovetxQViQJAEuhEuyNcVOIhOdrqzw9edRuwLFLw
kDtL1z9I2frENluRWEcpql1QKRoOgda7d68Hb6c3p6/mdmXEPvK+3MRV8QJBAIOL
2GlBC/oadAH8MURfPfBXU+7kdFsZUCNvJ4wbRQf3Vsr+4q32TkZxbMA1hzD7tVkv
HXNHFuWDe6E6/uhBawECQQC9xf7RGx4iD9PFpDiQ5YCqbDVocdWnaj8FT92LCTOr
9jCPXrf/gjSHDqL8iHpIKW5eekAFMU9dwMnS5mf/mERG
-----END RSA PRIVATE KEY-----";

        // ====== 42248（获取到期）密钥 ======
        private const string ExpiredPublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCjzqns+rkrTDLJg/bAu33A7BRT
MHC7tBnO0USQ/KIeZ7cMzN1LY2ZenfkAksS7u2ArXzQr2a3BuLGj9NOpVd6nHUIA
TggOV5Fesb1cGK+A4WGv+WEYduxeM0lSiJxGgorKU6sPDTGfBWOAnEGp/YkwF2Qz
oKokDfXYifJ9Qo2Z7QIDAQAB
-----END PUBLIC KEY-----";

        private const string ExpiredPrivateKeyPem = @"-----BEGIN RSA PRIVATE KEY-----
MIICWwIBAAKBgQCA9HO+uOMDl9FXDJS69WmKVplNKDJYrofjp2VwwEyT2dqh61xB
GEQDwOkqWnTCqq86nXGWhbYWA4abiALf28wCKnYGSgs+FxSmsixhqoS1WER6rHpt
XZ9Gk6+Pkaa8gXnj/jBWw3dClJSKSfnKhVs2G/YmTvODnX5JUOXR4VbenwIDAQAB
AoGAHk4Uc2JMy1fe1Pe6bjNlUaLnVQgYyNl/SuNYhgZTGL3uUPYYUJ3swfsQcKkg
Xm0xT/OXMZoLwCGk/SEaF9S2HQgpLW/CxxodAozklsfw79HCIvzGJ7TjsvYJa0oL
VZElp4CKM6wbOW6jMfCbdLddxXSc953j9JjImvn3G1PlWj0CQQDNI/X4N4G58pLT
ypnzpCawmCk9X49lzcR05CtqhmYLOM2ucP7joe6Vq48jbhi3BHnahNCNmWgfSHjX
690KBJ2VAkEAoO0Tnr2fcZnXzJxpFhD9HJMdjPjNFGBKqos+EI9rSg/Gtwo6o844
vyWbjcbe9kO02CDoIxyVCR+JcDUAFe62YwJAQ7+4oD1UrqCaNTAYIAr6bCAUnpxM
s4Z9d01TuV6hnNspso7G306/iNab80uNBgSIac6rQdiENrCsmELhQUm88QJABzyB
7Fp0iAQ1+wJxi0d6SkWnR4aMmkT2NpMKWG5KkcsB0YtJNcJ5NMc5Jnfx4LsMr8dT
CPkpDn73jC8l8NaKJwJAJf8z4s9yzFh+Ls8JmcKENdBFqG1cu34jaYVDGW0JTLa0
Gub4vGnVOOiXYW30aUbUnDg8jX7OuptVyeTEejI7aQ==
-----END RSA PRIVATE KEY-----";

        // ====== 42252（检测版本）密钥 ======
        private const string VersionPublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCCUd+JbgJ4QeQTqc1zLSa5Sze4
6n+roItclr0IQaWFIsNyMFGn3SM1xV5ega0xPMXD95ZYg/BEL3lMvjV2iobNWiqP
vVTSgOzkddA3FACN4Xih6dYMQCY5szEjYZom2XNDKBq1zfdL3GGHOa1bKKvj/i+3
W4pU118ep8+wuC8h8wIDAQAB
-----END PUBLIC KEY-----";

        private const string VersionPrivateKeyPem = @"-----BEGIN RSA PRIVATE KEY-----
MIICXAIBAAKBgQC9nx0TjZXb8HGRF/wJFrcJlpKbBN9OhX2+eRB3915eDM5HrVyb
4HdwNwMwfFKHs4CC0D3T0eZQeYvRMlNi2Nggfk/KNjSr2Zi1s1TT6/Ziwclog6mu
5QUu8MAp3/fSVxzInYVB20btY+l3A2QCF6C7MBwoosWen/+s3iuZ16tSSQIDAQAB
AoGAGIyS/RxlfGtX/fWMTGOZIC/9nFLjKOKgxu9xhwvgNxjn/ouejme0eYiwbFnh
bW7QgnRnV2xjVE6IICCofwbyjqOHHROvFs+Dwwl8Emz9SKCpTCtt6NafL6kEbSOx
R+vR59grYvKN1jRiawU2D0rS1RssXeycObbQZovtQcxw1WkCQQD6cfZG6tsrxMu5
M5jRKSZC9SnS1nMUEDEGLzbWJEh9ByqIZE3mbZ1AraDA0Q28PoT5baBhlijd+SRs
HJ+afLyrAkEAwdPK4Q/uIPzFOuftP7NrqoU/2wX+wIXE4omu6AA0boQjTM8xcuyj
+BbIkrf9mmtdt/68mpc9/RlW5KNKpYnE2wJAZ8B6dFqrPXCjrS/Q6SWQ8kA6eVva
BL/Ib3Vz1DbnyNQFLMfQ9dsHQFottHNmq0uDLwnZXVQlzf9+tUMOY6O1TQJAQaj2
jEFyQLiAM9FHfJHSQkS7ef3Q6/Uk2j0cBDm1iU64CpgRv0XM0gkdzx4HCh2e9OqV
h6T+edPwrKloayV9iwJBAJ1J428u/PEYWSMb8Jyg7RyrC8b3saos1ydwrBvqH/jT
fDtUjoyyOpaszlIH7bTwXJeD/6AHB255BFFYOizUjpU=
-----END RSA PRIVATE KEY-----";

        // ====== 42247（检测用户状态）密钥 ======
        private const string UserStatusPublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCNOpY7Zc68Zfd0SaOUx58mj+rE
cSjtmojNMri/bJSslhrkU+LuVd87U4ltZt5dNcqPGp5eKJMgrMoMQt8vJ4nDaIh0
28gQBpj7TPA3mIlc4z04qaXRP/y83wUFTH/E+xxSX/8NcgS5+dIlBPXqjUI4+D87
BIs5C6a++kHDRgvqSQIDAQAB
-----END PUBLIC KEY-----";

        private const string UserStatusPrivateKeyPem = @"-----BEGIN RSA PRIVATE KEY-----
MIICXAIBAAKBgQDHSaUIW3yE89gnsrB6qVnZ+sOtOkwewGa3aJ1sMeolngKPt1Pw
Fr/HzqiTM+SX2FGCZOrgqD52tvXFe9KOeOI5tDnwop+XgaHnlIRIva8Y2iLCiQr7
NXQx/fdBR0lj677J1aqnxF4X3FCGwvJLMI1t2+0/sAVNNyLLDQ/cdMBBiwIDAQAB
AoGAXqJRFRvkkHn+zjMjbMwYl9Nlsk/5r6yr2jJ6dtNpHf3ft4FWAa+72FUBZg2B
Yr3dTu8/PfmG1/bf1KyM+wzaV7f4PgJw8Uf91wWQNlfQIhV22w4LKYCJmBGdkibq
I//j5QdBV8IJhLCT7PMSfmO5eIMPQ4R0PjulXuKc00RC3/kCQQDmMr4lhMYRhRwH
G0wYrUOCqSKRr3zdpEHgZ+H0zyj48n2gGNJxMD/UHZTDNaEApO6tORVIEwAj9cSc
vB4FJjFvAkEA3Z/2Lbdo4vZYziFIMQig/J6x6oXrJFmR1ge0HIEqWz5u+qkYoGbv
K70BY8cEeTPeiqWqsTE/qMbBdL8xPJdrpQJBALfoNi03RB5fH6M11bepRNQwV+PY
NYPFZLPpioXQs0UgRekPq6CuEXBfKahDQhuHqP9PKYdpVqVkBe3KBJnMh5UCQA54
XkN34TJIcV3sEGGbNZ+o4Ob2HXc/HeWClUDzMgfJGMfm+IOolN8fNRMFsIYVW+dj
j7SICacEaycrQJS7Mj0CQBpmC6+i3Z7NrH1ZnZ3ID9sWKdaQRvCgRBBk9LqsD1CX
xnL0T+WAOcMEqt97OaF4RjCDdCrOjqD8UycM1elL13M=
-----END RSA PRIVATE KEY-----";

        // ====== 42251（获取变量）密钥 ======
        private const string VariablePublicKeyPem = @"-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCCFQ3slL5usVPVzvW6VQelOyfx
eUIzedpIGl42OeRrV+3ezOuqmCOnN8UwivCepk9sW7qc7FhH15jz5oi8ioS/q7OR
PmwLwFX4mtO8D7aMech2bXOnrVlPoOE6kxXWn0xekaWK8/UEsjyyv+VbNTv1ixIE
C/g0G72j/NqVNTgJPQIDAQAB
-----END PUBLIC KEY-----";

        private const string VariablePrivateKeyPem = @"-----BEGIN RSA PRIVATE KEY-----
MIICXAIBAAKBgQCG7tTUjMDeCVDKsH7ZOR0NWJliYNBs62oGLulkewjJ46wB/qlZ
+CXd8nBcmoF4jBeIaKGJjNql6rpALISXBn7ViJcsEfzWZNjYxyJtQU5i4KCtYyk+
03l0lIiHOtjwQiKpSHF7h4Ycbo8yStnT0GVu7HmnKx059OIFjHPDcwcT7QIDAQAB
AoGAQa87vehy7uN8B99ZMKdj5B5QNJrKe2syJqZpiTd3dMg28JWmnRx2Wo/tcLbp
9ePEhOviTxJZUdFtL1Y8iURpMxBb/dVgF8FsnkkdrFIhheQWUmwS0IqegyZ6HOxw
e12K+YJPnP1Yth7+iXvZr5ulrK6t7TGFA07IanJqFQvHdoUCQQDxrMPOts9PzGzd
8N771vd3i2OT0654dyTeqpvVWLsCH1HS2ZhHiaZo8Qm9EkbK+1ax2BZ/57AFbMTF
Tf3V8SaLAkEAju5WcBT7YOxsQARgE69RHCDQsAV79lPQ92AWPfAJW1dW/+pmP1KT
BjLD2n2cbuZheOMPh56eZBoQElbYUcX2ZwJAdiD+bPJKjzTbGfj85ZiDybkmaUGV
DGkgan52QWhnsHfipO+bUYxk/PKk0fg3BkyoabG3/bkf/ubVn6OpqYOAVQJANc2m
IB7l2cBlp1t+Ryqxn6MCq6AE398BRH2ZIcuf2hBwoXk88A1HZwfpDfBG2MBEZNsk
V2rwOMJO1nh2iaG5dQJBALvbpzdEEyWsqkO4GPF3ftGQcRZp1CNbzHE6aYJkmOYm
FN8W9M8D7a7EJ0wd3AB+ZZ5Tw8pDC7KydY8jsfxDwAw=
-----END RSA PRIVATE KEY-----";


        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;
        }

        void Update()
        {
            if (_mainQ.Count > 0)
            {
                Action a = null;
                lock (_lock) { if (_mainQ.Count > 0) a = _mainQ.Dequeue(); }
                if (a != null) { try { a(); } catch { } }
            }
        }

        private void PostMain(Action a)
        {
            lock (_lock) { _mainQ.Enqueue(a); }
        }

        // ===================== 入口：自动执行（可选登出）→版本→登录→到期→心跳 =====================
        public void RunAutoLogin(Action<bool, string> onDone)
        {
            Thread th = new Thread(() =>
            {
                try
                {
                    string iniPath = File.Exists(AbsIniPath) ? AbsIniPath : RelIniPath;

                    // (可选) 尝试登出旧会话
                    try
                    {
                        string oldCode = OperateIniFile.ReadIniData("root", "code", "", iniPath);
                        string oldUser = OperateIniFile.ReadIniData("root", "singlecode", "", iniPath);
                        if (!string.IsNullOrEmpty(oldCode) && !string.IsNullOrEmpty(oldUser))
                            TryLogoutIfNeeded(oldCode, oldUser);
                    }
                    catch { }

                    // 1) 读取 singlecode
                    string uSingleCode = OperateIniFile.ReadIniData("root", "singlecode", "", iniPath);
                    if (string.IsNullOrEmpty(uSingleCode))
                    {
                        //Fail("config.ini 中缺少 singlecode");
                        return;
                    }

                    // 2) 检测版本（加密）
                    if (!CheckAppVersionEncrypted(AppVersion, out var serverRaw))
                    {
                        //Fail("当前非最新版，请更新后再运行。");
                        return;
                    }
                   // FileLogger.Log("AUTH", "版本检测通过：服务器返回=" + serverRaw);

                    // 3) 登录（加密，严格顺序）
                    string mac = GetMacString();
                    var ordered = new List<KeyValuePair<string, string>> {
                        new KeyValuePair<string,string>("SingleCode", uSingleCode.Trim()),
                        new KeyValuePair<string,string>("Ver",        AppVersion),
                        new KeyValuePair<string,string>("mac",        mac)
                    };

                    if (!LoginEncrypted(ordered, out string statusCode))
                    {
                        //Fail("登录失败。");
                        return;
                    }

                    // 4) 成功：持久化 token 与 singlecode
                    OperateIniFile.WriteIniData("root", "code", statusCode, iniPath);
                    OperateIniFile.WriteIniData("root", "singlecode", uSingleCode.Trim(), iniPath);
                    SingleCode = uSingleCode.Trim();
                    Token = statusCode;
                    LoggedIn = true;

                    // 5) 拉取到期时间（不影响登录成功流程）
                    if (GetExpiredEncrypted(SingleCode, out var expire))
                    {
                        StaticExpiredText = expire;
                       // FileLogger.Log("AUTH", "获取到期时间成功：" + expire);
                    }
                    else
                    {
                      //  FileLogger.Log("AUTH", "获取到期时间失败。");
                    }

                    // 6) 成功回主线程启动业务 + 开启心跳
                    PostMain(() =>
                    {
                        try
                        {
                            StartAuthorizedEntry();
                            StartHeartbeat(20f); // 每 5 秒一次（后台异步）
                            onDone?.Invoke(true, null);
                        }
                        catch (Exception ex)
                        {
                            onDone?.Invoke(false, "启动业务失败：" + ex.Message);
                        }
                    });
                }
                catch (Exception ex)
                {
                  //  Fail("网络/登录异常：" + ex.Message);
                }
            });

            th.IsBackground = true;
            th.Start();

            void Fail(string msg)
            {
                LoggedIn = false;
                LastError = msg;
                PostMain(() =>
                {
                    onDone?.Invoke(false, msg);
                    try { Application.Quit(); } catch { }
                    try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
                });
            }
        }

        /// <summary>查找并调用任意一个实现了 IAuthorizedEntry 的组件。</summary>
        private void StartAuthorizedEntry()
        {
            //var all = (MonoBehaviour[])GameObject.FindObjectsOfType(typeof(MonoBehaviour));
            //if (all != null)
            //{
            //    for (int i = 0; i < all.Length; i++)
            //    {
            //        var entry = all[i] as IAuthorizedEntry;
            //        if (entry != null)
            //        {
            //            entry.OnAuthorizedStart();
            //            Debug.Log("[Auth] Authorized entry started: " + all[i].GetType().FullName);
            //            return;
            //        }
            //    }
            //}
            //Debug.Log("[Auth] 登录成功，但未找到 IAuthorizedEntry。请将入口组件挂到场景对象上。");
        }

        // ============================= 对外方法 =============================

        /// <summary>检测是否最新版（42252）。true=最新版；false=非最新版或失败。</summary>
        public bool CheckAppVersionEncrypted(string ver, out string serverRaw)
        {
            var ordered = new List<KeyValuePair<string, string>> {
                new KeyValuePair<string,string>("ver", ver ?? "")
            };
            if (CallEncryptedApi(VersionUrl, "42252", VersionPublicKeyPem, VersionPrivateKeyPem, ordered, out var plain))
            {
                // 服务端统一“结果|时间”，结果为 "1"(最新版) 或 "0"(不是)
                string head = ExtractHead(plain);
                serverRaw = head;
                return head == "1";
            }
            serverRaw = null;
            return false;
        }

        /// <summary>加密登录：ordered 参数顺序必须为 SingleCode, Ver, mac。成功输出 32 位状态码。</summary>
        public bool LoginEncrypted(List<KeyValuePair<string, string>> ordered, out string statusCode)
        {
            statusCode = null;
            if (!CallEncryptedApi(LoginUrl, "42246", ServerPublicKeyPem, ServerPrivateKeyPem, ordered, out var plain))
                return false;

            string head = ExtractHead(plain);
            if (!string.IsNullOrEmpty(head) && head.Length == 32)
            {
                statusCode = head;
                return true;
            }
            return false;
        }

        /// <summary>获取到期时间（42248）。传入 32 位状态码，返回左侧“到期字符串”。</summary>
        public bool GetExpiredEncrypted(string statusCode, out string expiredText)
        {
            expiredText = null;
            if (string.IsNullOrEmpty(statusCode) || statusCode.Length != 32) return false;

            // 与 C++ 一致：StatusCode=xxxx
            var ordered = new List<KeyValuePair<string, string>> {
                new KeyValuePair<string,string>("UserName", statusCode)
            };

            if (!CallEncryptedApi(ExpiredUrl, "42248", ExpiredPublicKeyPem, ExpiredPrivateKeyPem, ordered, out var plain))
                return false;

            expiredText = ExtractHead(plain);
            StaticExpiredText = expiredText;
            return !string.IsNullOrEmpty(expiredText);
        }

        /// <summary>退出登录（42250）。传入 32 位状态码和卡密。（静默，不抛异常）</summary>
        public void TryLogoutIfNeeded(string StatusCode, string SingleCode)
        {
            try
            {
                if (!string.IsNullOrEmpty(StatusCode) && !string.IsNullOrEmpty(SingleCode))
                {
                    // 严格顺序：StatusCode, UserName
                    var ordered = new List<KeyValuePair<string, string>> {
                        new KeyValuePair<string,string>("StatusCode", StatusCode),
                        new KeyValuePair<string,string>("UserName",   SingleCode)
                    };
                    // 注意：走 LogoutUrl + api=42250
                    _ = CallEncryptedApi(LogoutUrl, "42250", LogoutPublicKeyPem, LogoutPrivateKeyPem, ordered, out _);
                }
            }
            catch { /* 忽略登出失败 */ }
        }

        /// <summary>检测用户当前状态（42247）。传入 32 位状态码和卡密，成功返回 true（结果头为 "1"）。</summary>
        public bool CheckUserStatus(string StatusCode, string SingleCode)
        {
            try
            {
                if (!string.IsNullOrEmpty(StatusCode) && !string.IsNullOrEmpty(SingleCode))
                {
                    // 严格顺序：StatusCode, UserName
                    var ordered = new List<KeyValuePair<string, string>> {
                        new KeyValuePair<string,string>("StatusCode", StatusCode),
                        new KeyValuePair<string,string>("UserName",   SingleCode)
                    };
                    if (!CallEncryptedApi(UserStatusUrl, "42247", UserStatusPublicKeyPem, UserStatusPrivateKeyPem, ordered, out var plain))
                        return false;

                    var sta = ExtractHead(plain);
                    //FileLogger.Log("心跳", sta);
                    return sta == "1";
                }
            }
            catch { }
            return false;
        }

        /// <summary>获取变量数据（42251）。</summary>
        public bool GetVariableEncrypted(string statusCode, string SingleCode, string VariableId, string VariableName, out string v)
        {
            v = null;
            if (string.IsNullOrEmpty(statusCode) || statusCode.Length != 32) return false;

            var ordered = new List<KeyValuePair<string, string>> {
                new KeyValuePair<string,string>("StatusCode", statusCode),
                new KeyValuePair<string,string>("UserName", SingleCode),
                new KeyValuePair<string,string>("VariableId", VariableId),
                new KeyValuePair<string,string>("VariableName", VariableName)
            };

            if (!CallEncryptedApi(VariableUrl, "42251", VariablePublicKeyPem, VariablePrivateKeyPem, ordered, out var plain))
                return false;

            v = ExtractHead(plain);
            FileLogger.Log("获取变量", v);
            return !string.IsNullOrEmpty(v);
        }

        // ============================ 心跳：协程调度 + 线程池执行 ============================

        // 新增：连续失败次数
        private int _hbFailCount = 0;

        public void StartHeartbeat(float seconds)
        {
            StopHeartbeat();
            _hbFailCount = 0;        // 启动前清零
            _hbCo = StartCoroutine(HeartbeatLoop(seconds));
        }

        public void StopHeartbeat()
        {
            if (_hbCo != null) { StopCoroutine(_hbCo); _hbCo = null; }
            _hbInFlight = false;
            _hbFailCount = 0;        // 停止时清零，避免下次沿用
        }

        private System.Collections.IEnumerator HeartbeatLoop(float interval)
        {
            float waitSec = Mathf.Max(1f, interval);

            while (true)
            {
                // Token/SingleCode 为空时，不做心跳，顺便清零失败计数
                if (string.IsNullOrEmpty(Token) || string.IsNullOrEmpty(SingleCode))
                {
                    _hbFailCount = 0;
                }
                else if (!_hbInFlight)
                {
                    _hbInFlight = true;

                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        bool ok = false;

                        try
                        {
                            // 第一次尝试
                            ok = CheckUserStatus(Token, SingleCode);
                            if (!ok)
                            {
                                // 失败则立即再试一次
                                try
                                {
                                    ok = CheckUserStatus(Token, SingleCode);
                                }
                                catch { ok = false; }
                            }
                        }
                        catch { ok = false; }

                        // 回主线程处理结果与可能的退出
                        PostMain(() =>
                        {
                            _hbInFlight = false;

                            if (ok)
                            {
                                //if (_hbFailCount != 0)
                                    //FileLogger.Log("AUTH", $"心跳恢复成功（之前连续失败：{_hbFailCount} 次）。");
                                _hbFailCount = 0; // 成功清零
                            }
                            else
                            {
                                _hbFailCount++;
                                //FileLogger.Log("AUTH", $"心跳失败（连续第 {_hbFailCount} 次）。");

                                if (_hbFailCount >= 3)
                                {
                                    //FileLogger.Log("AUTH", "心跳连续三次失败，退出程序。");
                                    try { Application.Quit(); } catch { }
                                    try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
                                }
                            }
                        });
                    });
                }

                // 用非缩放时间等待
                yield return WaitRealtime(waitSec);
            }
        }

        private System.Collections.IEnumerator WaitRealtime(float seconds)
        {
            float end = Time.realtimeSinceStartup + Mathf.Max(0f, seconds);
            while (Time.realtimeSinceStartup < end)
                yield return null;
        }



        // ============================ 统一加密/解密调用 ============================
        private bool CallEncryptedApi(
            string url,
            string apiId,
            string pemPublic,
            string pemPrivate,
            List<KeyValuePair<string, string>> orderedParameters,
            out string outPlain)
        {
            outPlain = null;

            // 1) 明文（严格按顺序）
            string plain = BuildPlainQuery(orderedParameters);

            // 2) 生成 p（滚动 XOR + RSA/PKCS1 + Base64），keys 用于回包 XOR
            var keys = new List<int>();
            string p = EncryptWithPem(pemPublic, plain, keys);
            if (string.IsNullOrEmpty(p)) return false;

            // 3) form：p & api
            var form = new Dictionary<string, string> {
                { "p",   p    },
                { "api", apiId}
            };

            // 4) POST（同步执行，但心跳等走线程池，不阻塞主线程）
            string respRaw = ApiPost(url, form);
            if (string.IsNullOrEmpty(respRaw)) return false;

            // 5) 解密（RSA 私钥 + 与请求相同的 keys 做回滚 XOR）
            string plainResp = DecryptWithPem(pemPrivate, respRaw, keys);
            if (string.IsNullOrEmpty(plainResp)) return false;

            outPlain = plainResp;
            return true;
        }

        // =============================== HTTP 工具 ===============================
        private static bool AlwaysAllowCert(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors err)
        { return true; }

        private string ApiPost(string url, IDictionary<string, string> parameters)
        {
            try
            {
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | SecurityProtocolType.Tls;
            }
            catch { }
            ServicePointManager.ServerCertificateValidationCallback = AlwaysAllowCert;

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url.Trim());
            req.ProtocolVersion = HttpVersion.Version10; // 与原始实现保持一致
            req.Method = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.UserAgent = "Mozilla/4.0 (compatible; MSIE 6.0; Windows NT 5.2)";

            byte[] body = new byte[0];
            if (parameters != null && parameters.Count > 0)
            {
                StringBuilder sb = new StringBuilder();
                foreach (var kv in parameters)
                {
                    if (sb.Length > 0) sb.Append('&');
                    sb.Append(UrlEncode(kv.Key)).Append('=').Append(UrlEncode(kv.Value ?? ""));
                }
                body = Encoding.UTF8.GetBytes(sb.ToString());
            }

            using (var stream = req.GetRequestStream()) { stream.Write(body, 0, body.Length); }
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var rs = resp.GetResponseStream())
            using (var sr = new StreamReader(rs, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        private static string UrlEncode(string s)
        {
            if (s == null) return "";
            return Uri.EscapeDataString(s);
        }

        private static string GetMacString()
        {
            try { return SystemInfo.deviceUniqueIdentifier ?? ""; } catch { return ""; }
        }

        // ======================= BouncyCastle + 协议封装 ==========================
        private static AsymmetricKeyParameter ImportPempublicKey(string pemString)
        {
            var pemReader = new PemReader(new StringReader(pemString));
            var objectRead = pemReader.ReadObject();
            if (objectRead is AsymmetricCipherKeyPair s) return s.Public;
            var k = objectRead as AsymmetricKeyParameter;
            if (k == null) throw new InvalidOperationException("PEM string does not contain a recognized public key.");
            return k;
        }

        private static AsymmetricKeyParameter ImportPemPrivateKey(string pemString)
        {
            var pemReader = new PemReader(new StringReader(pemString));
            var objectRead = pemReader.ReadObject();
            if (objectRead is AsymmetricCipherKeyPair s) return s.Private;
            var k = objectRead as AsymmetricKeyParameter;
            if (k == null) throw new InvalidOperationException("PEM string does not contain a recognized private key.");
            return k;
        }

        // 通用加密：滚动 XOR → [len][keys...] 前缀 → 分段 RSA/PKCS1 → Base64
        private static string EncryptWithPem(string pemPublic, string value, List<int> keys)
        {
            AsymmetricKeyParameter publicKey = ImportPempublicKey(pemPublic);
            var pkcs1 = new Pkcs1Encoding(new RsaEngine());
            pkcs1.Init(true, publicKey);

            int keySizeBytes = 1024 / 8;
            int blockSize = keySizeBytes - 11;

            var rnd = new System.Random();
            keys.Clear();
            int keys_length = rnd.Next(3, 6);
            for (int i = 0; i < keys_length; i++) keys.Add(rnd.Next(1, 255));
            if (keys.Count > 8) keys = keys.Take(8).ToList();

            byte[] data = Encoding.UTF8.GetBytes(value);
            for (int i = 0; i < keys.Count; i++)
            {
                int _key = keys[i] % 256;
                for (int j = 0; j < data.Length; j++) data[j] = (byte)(data[j] ^ _key);
            }

            byte[] head = new byte[] { (byte)keys.Count }.Concat(keys.Select(p => (byte)p)).ToArray();
            byte[] originalDataBytes = head.Concat(data).ToArray();

            byte[] encryptedData = new byte[0];
            int offset = 0;
            while (offset < originalDataBytes.Length)
            {
                int bl = Math.Min(blockSize, originalDataBytes.Length - offset);
                byte[] block = new byte[bl];
                Buffer.BlockCopy(originalDataBytes, offset, block, 0, bl);
                byte[] enc = pkcs1.ProcessBlock(block, 0, block.Length);
                encryptedData = encryptedData.Concat(enc).ToArray();
                offset += bl;
            }

            return Convert.ToBase64String(encryptedData);
        }

        // 通用解密：Base64 → 分段 RSA/PKCS1 → 滚动 XOR 还原
        private static string DecryptWithPem(string pemPrivate, string value, List<int> keys)
        {
            if (keys.Count > 8) keys = keys.Take(8).ToList();

            AsymmetricKeyParameter privateKey = ImportPemPrivateKey(pemPrivate);
            var pkcs1 = new Pkcs1Encoding(new RsaEngine());
            pkcs1.Init(false, privateKey);

            int keySizeBytes = 1024 / 8;
            int blockSize = keySizeBytes;

            byte[] cipher = Convert.FromBase64String(value);
            byte[] plain = new byte[0];

            int offset = 0;
            while (offset < cipher.Length)
            {
                int bl = Math.Min(blockSize, cipher.Length - offset);
                byte[] block = new byte[bl];
                Buffer.BlockCopy(cipher, offset, block, 0, bl);
                byte[] dec = pkcs1.ProcessBlock(block, 0, block.Length);
                plain = plain.Concat(dec).ToArray();
                offset += bl;
            }

            for (int i = 0; i < keys.Count; i++)
            {
                int _key = keys[i] % 256;
                for (int j = 0; j < plain.Length; j++) plain[j] = (byte)(plain[j] ^ _key);
            }
            return Encoding.UTF8.GetString(plain);
        }

        // ======= 构造“严格顺序”明文 =======
        private static string BuildPlainQuery(List<KeyValuePair<string, string>> ordered)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < ordered.Count; i++)
            {
                if (i > 0) sb.Append('&');
                sb.Append(ordered[i].Key).Append('=').Append(ordered[i].Value ?? "");
            }
            return sb.ToString();
        }

        // ======= 统一处理：取 “结果|时间” 的左侧 =======
        private static string ExtractHead(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int bar = s.IndexOf('|');
            string head = (bar >= 0) ? s.Substring(0, bar) : s;
            return head.Trim();
        }
    }
}
