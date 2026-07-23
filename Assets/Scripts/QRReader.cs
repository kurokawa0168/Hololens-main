using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using ZXing;

public class QRReader : MonoBehaviour
{
    private CameraFeed cameraFeed;
    private IBarcodeReader barcodeReader;
    private Transform mainCameraTransform;

    [Header("UI Prefab Settings")]
    [Tooltip("請把剛剛做好的 AR_Panel Prefab 拖到這裡")]
    public GameObject arPanelPrefab;   

    [Header("Detection Settings")]
    [Range(0.05f, 2f)]
    public float scanInterval = 0.1f;  // 掃描頻率
    private bool isScanning = false;

    // 用於追蹤目前畫面上所有產生的 AR 面板
    private class ActivePanel
    {
        public GameObject panelInstance;
        public TMP_Text titleText;
        public TMP_Text statusText;
        public Vector3 targetLocalPosition;
        public Vector3 targetLocalScale;
        public float lastSeenTime;
    }

    private Dictionary<string, ActivePanel> activePanels = new Dictionary<string, ActivePanel>();
    private const float hideDelay = 1.0f; // 超過 1 秒沒掃到該 QR 碼就自動刪除面板

    void Start()
    {
        cameraFeed = FindObjectOfType<CameraFeed>();
        if (cameraFeed == null)
        {
            Debug.LogError("❌ 找不到 CameraFeed 腳本！");
        }

        if (Camera.main != null)
        {
            mainCameraTransform = Camera.main.transform;
        }
        else
        {
            Debug.LogError("❌ 找不到主相機！");
        }

        // 初始化 ZXing 解碼器
        barcodeReader = new BarcodeReader
        {
            AutoRotate = true,
            Options = new ZXing.Common.DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
            }
        };

        StartCoroutine(ScanQRRoutine());
    }

    void Update()
    {
        // ⚡ 在每幀平滑更新所有存在面板的位置與縮放
        List<string> keysToRemove = new List<string>();

        foreach (var kvp in activePanels)
        {
            string qrKey = kvp.Key;
            ActivePanel panel = kvp.Value;

            // 如果該 QR Code 消失超過指定時間，就準備將它刪除
            if (Time.time - panel.lastSeenTime > hideDelay)
            {
                Destroy(panel.panelInstance);
                keysToRemove.Add(qrKey);
                continue;
            }

            #if UNITY_EDITOR
            // 💻 【電腦編輯器模擬環境測試】
            if (panel.panelInstance.transform.parent != mainCameraTransform)
            {
                panel.panelInstance.transform.SetParent(mainCameraTransform, true);
            }

            // 絲滑 Lerp 跟隨
            panel.panelInstance.transform.localPosition = Vector3.Lerp(
                panel.panelInstance.transform.localPosition, 
                panel.targetLocalPosition, 
                Time.deltaTime * 12f
            );

            // 轉回正面
            panel.panelInstance.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);

            // 絲滑近大遠小縮放
            panel.panelInstance.transform.localScale = Vector3.Lerp(
                panel.panelInstance.transform.localScale, 
                panel.targetLocalScale, 
                Time.deltaTime * 12f
            );
            #else
            // 👓 【HoloLens 2 實機部署環境】
            if (panel.panelInstance.transform.parent != null)
            {
                panel.panelInstance.transform.SetParent(null);
            }

            panel.panelInstance.transform.position = Vector3.Lerp(
                panel.panelInstance.transform.position, 
                panel.targetLocalPosition, 
                Time.deltaTime * 12f
            );
            panel.panelInstance.transform.LookAt(mainCameraTransform.position);
            panel.panelInstance.transform.Rotate(0, 180, 0);
            #endif
        }

        // 清除已經消失的面板資料
        foreach (var key in keysToRemove)
        {
            activePanels.Remove(key);
        }
    }

    IEnumerator ScanQRRoutine()
    {
        while (true)
        {
            if (cameraFeed != null && cameraFeed.cam != null && cameraFeed.cam.isPlaying && !isScanning)
            {
                yield return StartCoroutine(DecodeQRFrame());
            }
            yield return new WaitForSeconds(scanInterval);
        }
    }

    IEnumerator DecodeQRFrame()
    {
        isScanning = true;

        WebCamTexture webCamTex = cameraFeed.cam;
        int width = webCamTex.width;
        int height = webCamTex.height;

        if (width < 100 || height < 100)
        {
            isScanning = false;
            yield break;
        }

        Color32[] c32 = null;
        try
        {
            c32 = webCamTex.GetPixels32();
        }
        catch (Exception ex)
        {
            Debug.LogWarning("讀取 Webcam 像素失敗: " + ex.Message);
        }

        if (c32 != null)
        {
            Result[] results = null;
            bool done = false;

            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // 🌟 核心：改用 DecodeMultiple 同時辨識多個 QR Code！
                    results = barcodeReader.DecodeMultiple(c32, width, height);
                }
                catch { }
                done = true;
            });

            while (!done) yield return null;

            if (results != null && results.Length > 0)
            {
                foreach (var result in results)
                {
                    string rawText = result.Text;
                    string[] parts = rawText.Split(',');

                    string seatId = rawText;
                    string status = "Unknown";

                    if (parts.Length == 2)
                    {
                        seatId = parts[0].Trim();
                        status = parts[1].Trim();
                    }

                    UpdateOrCreatePanel(rawText, seatId, status, result.ResultPoints, width, height);
                }
            }
        }

        isScanning = false;
    }

    private void UpdateOrCreatePanel(string qrKey, string seatId, string status, ResultPoint[] points, int camWidth, int camHeight)
    {
        if (arPanelPrefab == null || points == null || points.Length == 0) return;

        // 1. 計算 QR Code 的中心點
        float sumX = 0, sumY = 0;
        foreach (var p in points)
        {
            sumX += p.X;
            sumY += p.Y;
        }
        float centerX = sumX / points.Length;
        float centerY = sumY / points.Length;

        // 2. 計算目標位置與縮放
        Vector3 calculatedLocalPos;
        Vector3 calculatedLocalScale;

        #if UNITY_EDITOR
        // 💻 電腦模擬計算
        float realWidth = camWidth;
        float realHeight = camHeight;
        if (cameraFeed != null && cameraFeed.cam != null)
        {
            realWidth = cameraFeed.cam.width;
            realHeight = cameraFeed.cam.height;
        }

        float offsetX = (centerX / realWidth) - 0.5f;
        float offsetY = (centerY / realHeight) - 0.5f;

        calculatedLocalPos = new Vector3(offsetX * 0.6f, offsetY * 0.6f - 0.05f, 0.5f);

        // 📐 動態縮放（近大遠小）
        float qrWidthInPixels = 100f;
        if (points.Length >= 2)
        {
            qrWidthInPixels = Vector2.Distance(new Vector2(points[0].X, points[0].Y), new Vector2(points[1].X, points[1].Y));
        }
        float dynamicScaleFactor = Mathf.Clamp(qrWidthInPixels * 0.000008f, 0.0005f, 0.0018f);
        calculatedLocalScale = new Vector3(dynamicScaleFactor, dynamicScaleFactor, dynamicScaleFactor);
        #else
        // 👓 HoloLens 2 實機計算
        Vector3 normalizedScreenPos = new Vector3(centerX / camWidth, centerY / camHeight, 0f);
        Vector3 viewPortPos = new Vector3(normalizedScreenPos.x, normalizedScreenPos.y, 1.0f);
        Vector3 targetWorldPos = Camera.main.ViewportToWorldPoint(viewPortPos);
        targetWorldPos += Vector3.up * 0.1f;

        calculatedLocalPos = targetWorldPos;
        calculatedLocalScale = new Vector3(0.001f, 0.001f, 0.001f); // 實機使用固定縮放
        #endif

        // 3. 更新或創建面板實例
        if (activePanels.TryGetValue(qrKey, out ActivePanel existingPanel))
        {
            // 已存在面板：更新目標資料與看到的時間
            existingPanel.targetLocalPosition = calculatedLocalPos;
            existingPanel.targetLocalScale = calculatedLocalScale;
            existingPanel.lastSeenTime = Time.time;
            UpdateTextDisplay(existingPanel.titleText, existingPanel.statusText, seatId, status);
        }
        else
        {
            // 🆕 建立新面板：實例化 Prefab
            GameObject newPanelObj = Instantiate(arPanelPrefab);
            
            // 🌟 【加在這一行！】強制把複製出來的面板勾勾打開
            newPanelObj.SetActive(true);
            
            // 尋找子物件中的 Title 和 Status 元件
            TMP_Text titleComponent = newPanelObj.transform.Find("Title")?.GetComponent<TMP_Text>();
            TMP_Text statusComponent = newPanelObj.transform.Find("Status")?.GetComponent<TMP_Text>();

            if (titleComponent == null || statusComponent == null)
            {
                // 如果找不到，嘗試使用 GetComponentsInChildren 作為備案
                TMP_Text[] tmps = newPanelObj.GetComponentsInChildren<TMP_Text>();
                if (tmps.Length >= 2)
                {
                    titleComponent = tmps[0];
                    statusComponent = tmps[1];
                }
            }

            // 初始化位置與縮放，避免生成時瞬移過大
            newPanelObj.transform.localPosition = calculatedLocalPos;
            newPanelObj.transform.localScale = calculatedLocalScale;

            UpdateTextDisplay(titleComponent, statusComponent, seatId, status);

            ActivePanel newPanel = new ActivePanel
            {
                panelInstance = newPanelObj,
                titleText = titleComponent,
                statusText = statusComponent,
                targetLocalPosition = calculatedLocalPos,
                targetLocalScale = calculatedLocalScale,
                lastSeenTime = Time.time
            };

            activePanels.Add(qrKey, newPanel);
        }
    }

    private void UpdateTextDisplay(TMP_Text titleTxt, TMP_Text statusTxt, string seatId, string status)
    {
        if (titleTxt != null) titleTxt.text = "Seat: " + seatId;
        if (statusTxt != null)
        {
            if (status == "Green" || status == "Available")
            {
                statusTxt.text = "<color=green>Status: Available</color>";
            }
            else if (status == "Red" || status == "Occupied")
            {
                statusTxt.text = "<color=red>Status: Occupied</color>";
            }
            else
            {
                statusTxt.text = "<color=yellow>Status: " + status + "</color>";
            }
        }
    }
}