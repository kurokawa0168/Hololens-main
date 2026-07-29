using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using TMPro;
using ZXing;

// --- JSON 資料結構定義（對應 MongoDB 欄位）---
[System.Serializable]
public class UserData
{
    public string account;
    public string name;
    public string role;
    public string U_id;
    public string user_id;
    public int seat_number;
}

[System.Serializable]
public class UserApiResponse
{
    public bool success;
    public string seatId;
    public string status;
    public UserData user;
    public string message;
}

public class QRReader : MonoBehaviour
{
    [Header("UI 畫面視覺元件")]
    public RawImage rawImageBackground;
    public AspectRatioFitter aspectRatioFitter;
    public RectTransform scanZone;
    public TextMeshProUGUI statusText;
    public TextMeshProUGUI qrResultText;
    public TextMeshProUGUI debugLogText;

    [Header("網路 API 設定")]
    [Tooltip("請輸入 API 基本 URL，例如 http://127.0.0.1:8080/api/users/")]
    public string apiBaseUrl = "http://127.0.0.1:8080/api/users/";

    [Header("掃描參數設定")]
    public bool enableAutoScan = true;
    public float scanInterval = 0.5f; // 解碼間隔秒數
    public bool showDebugGUI = true;

    // --- 攝影機控制變數 ---
    private WebCamTexture webCamTexture;
    private bool isCamAvailable = false;

    // --- 多線程 (Threading) 異步解碼變數 ---
    private Thread qrDecodeThread;
    private Color32[] c32Data;
    private int W, H;
    private bool isDecoding = false;
    private string decodedResultText = "";
    private bool hasNewResult = false;

    // --- ZXing 解碼器與連線鎖定 ---
    private IBarcodeReader barcodeReader;
    private bool isProcessingApi = false;
    private string lastScannedCode = "";
    private float lastScanTime = 0f;

    void Start()
    {
        LogToConsoleAndUI("🚀 初始化 QRReader 系統中...");

        // 1. 初始化 ZXing 核心設定
        barcodeReader = new BarcodeReader
        {
            AutoRotate = true,
            Options = new ZXing.Common.DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
            }
        };

        // 2. 初始化鏡頭
        InitializeWebCam();

        // 3. 啟動背景定期掃描 Coroutine
        StartCoroutine(ScanLoop());
    }

    void InitializeWebCam()
    {
        WebCamDevice[] devices = WebCamTexture.devices;
        if (devices.Length == 0)
        {
            LogToConsoleAndUI("⚠️ 未偵測到可用攝影機！可按 T / Y 鍵進行模擬測試。");
            return;
        }

        for (int i = 0; i < devices.Length; i++)
        {
            if (!devices[i].isFrontFacing)
            {
                webCamTexture = new WebCamTexture(devices[i].name, 1280, 720);
                break;
            }
        }

        if (webCamTexture == null && devices.Length > 0)
        {
            webCamTexture = new WebCamTexture(devices[0].name, 1280, 720);
        }

        if (webCamTexture != null)
        {
            webCamTexture.Play();
            if (rawImageBackground != null)
            {
                rawImageBackground.texture = webCamTexture;
            }
            isCamAvailable = true;
            LogToConsoleAndUI("📷 攝影機啟動成功！");
        }
    }

    void Update()
    {
        // =============================================================
        // 🧪 測試按鍵邏輯：對應雲端 MongoDB 真實存在的帳號 (e001 / s001)
        // =============================================================
        if (Input.GetKeyDown(KeyCode.T))
        {
            LogToConsoleAndUI("🧪 [測試模式] 按下 T 鍵 -> 模擬掃描教師帳號: e001");
            OnQRCodeScanned("e001");
        }

        if (Input.GetKeyDown(KeyCode.Y))
        {
            LogToConsoleAndUI("🧪 [測試模式] 按下 Y 鍵 -> 模擬掃描學生帳號: s001");
            OnQRCodeScanned("s001");
        }

        if (Input.GetKeyDown(KeyCode.U))
        {
            LogToConsoleAndUI("🧪 [測試模式] 按下 U 鍵 -> 模擬掃描學生帳號: s002");
            OnQRCodeScanned("s002");
        }

        if (Input.GetKeyDown(KeyCode.I))
        {
            LogToConsoleAndUI("🧪 [測試模式] 按下 I 鍵 -> 模擬掃描學生帳號: s003");
            OnQRCodeScanned("s003");
        }
        // =============================================================

        // 更新鏡頭顯示比例與轉向
        if (isCamAvailable && webCamTexture != null && webCamTexture.isPlaying)
        {
            UpdateCameraAspectRatio();
        }

        // 檢查背景 Thread 是否已完成 QR Code 解碼
        if (hasNewResult)
        {
            hasNewResult = false;
            if (!string.IsNullOrEmpty(decodedResultText))
            {
                LogToConsoleAndUI($"🎯 鏡頭異步解碼成功: {decodedResultText}");
                OnQRCodeScanned(decodedResultText);
            }
        }
    }

    void UpdateCameraAspectRatio()
    {
        if (webCamTexture.width < 100) return;

        float ratio = (float)webCamTexture.width / (float)webCamTexture.height;
        if (aspectRatioFitter != null)
        {
            aspectRatioFitter.aspectRatio = ratio;
        }

        int orient = -webCamTexture.videoRotationAngle;
        if (rawImageBackground != null)
        {
            rawImageBackground.rectTransform.localEulerAngles = new Vector3(0, 0, orient);
        }
    }

    IEnumerator ScanLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(scanInterval);

            if (enableAutoScan && isCamAvailable && webCamTexture != null && webCamTexture.isPlaying && !isDecoding && !isProcessingApi)
            {
                c32Data = webCamTexture.GetPixels32();
                W = webCamTexture.width;
                H = webCamTexture.height;

                if (c32Data != null && c32Data.Length > 0)
                {
                    isDecoding = true;
                    qrDecodeThread = new Thread(DecodeQRThread);
                    qrDecodeThread.Start();
                }
            }
        }
    }

    void DecodeQRThread()
    {
        try
        {
            var result = barcodeReader.Decode(c32Data, W, H);
            if (result != null)
            {
                decodedResultText = result.Text;
                hasNewResult = true;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("Thread 解碼異常: " + ex.Message);
        }
        finally
        {
            isDecoding = false;
        }
    }

    public void OnQRCodeScanned(string qrContent)
    {
        if (string.IsNullOrEmpty(qrContent)) return;

        // 防重複發送連擊判斷 (3秒冷卻)
        if (isProcessingApi || (qrContent == lastScannedCode && Time.time - lastScanTime < 3.0f))
            return;

        lastScannedCode = qrContent;
        lastScanTime = Time.time;
        isProcessingApi = true;

        if (qrResultText != null) qrResultText.text = "掃描結果: " + qrContent;
        LogToConsoleAndUI($"📡 正在發送 API 請求 -> {qrContent}");

        string targetUrl = apiBaseUrl + qrContent.Trim();
        StartCoroutine(FetchUserDataCoroutine(targetUrl));
    }

    IEnumerator FetchUserDataCoroutine(string url)
    {
        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 5;
            yield return request.SendWebRequest();

            // 僅有在「網路物理斷線/伺服器未開啟」時認定為連線錯誤
            if (request.result == UnityWebRequest.Result.ConnectionError)
            {
                LogToConsoleAndUI($"❌ 網路斷線錯誤: {request.error}");
                if (statusText != null) statusText.text = "連線失敗: " + request.error;
            }
            else
            {
                // 允許讀取 HTTP 200 及 HTTP 404 (查無使用者) 的 JSON 內容
                string jsonResult = request.downloadHandler.text;
                LogToConsoleAndUI($"✅ 收到回應 (HTTP {request.responseCode}):\n{jsonResult}");

                if (!string.IsNullOrEmpty(jsonResult))
                {
                    ProcessApiResponse(jsonResult);
                }
            }
        }

        yield return new WaitForSeconds(1.5f);
        isProcessingApi = false;
    }

    void ProcessApiResponse(string json)
    {
        try
        {
            UserApiResponse response = JsonUtility.FromJson<UserApiResponse>(json);

            if (response != null && response.success)
            {
                // 組合完整可視化資訊
                string displayInfo = $"【座位/帳號】: {response.seatId}\n" +
                                     $"【使用狀態】: {(response.status == "Occupied" ? "🔴 使用中" : "🟢 空位")}";

                if (response.user != null)
                {
                    displayInfo += $"\n【使用者姓名】: {response.user.name}\n" +
                                   $"【帳號角色】: {response.user.role}\n" +
                                   $"【帳號 ID】: {response.user.account}";
                }

                LogToConsoleAndUI($"🎉 資料讀取成功:\n{displayInfo}");

                if (statusText != null)
                {
                    statusText.text = displayInfo;
                }
            }
            else
            {
                string errorMsg = response != null ? response.message : "JSON 解析異常";
                LogToConsoleAndUI($"⚠️ 後端回傳訊息: {errorMsg}");
                if (statusText != null) statusText.text = errorMsg;
            }
        }
        catch (Exception ex)
        {
            LogToConsoleAndUI($"❌ JSON 解析例外: {ex.Message}");
        }
    }

    void LogToConsoleAndUI(string message)
    {
        Debug.Log(message);
        if (debugLogText != null)
        {
            debugLogText.text = $"[{DateTime.Now:HH:mm:ss}] {message}\n" + debugLogText.text;
        }
    }

    void OnGUI()
    {
        if (!showDebugGUI) return;

        GUI.Box(new Rect(10, 10, 220, 150), "QR Reader 測試面板");
        if (GUI.Button(new Rect(20, 40, 200, 30), "測試 e001 (教師, 按 T)"))
        {
            OnQRCodeScanned("e001");
        }
        if (GUI.Button(new Rect(20, 80, 200, 30), "測試 s001 (學生, 按 Y)"))
        {
            OnQRCodeScanned("s001");
        }
        if (GUI.Button(new Rect(20, 120, 200, 20), "清空 Log"))
        {
            if (debugLogText != null) debugLogText.text = "";
        }
    }

    void OnDestroy()
    {
        if (qrDecodeThread != null && qrDecodeThread.IsAlive)
        {
            qrDecodeThread.Abort();
        }

        if (webCamTexture != null && webCamTexture.isPlaying)
        {
            webCamTexture.Stop();
        }
    }
}