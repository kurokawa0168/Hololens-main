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
    [Tooltip("請把做好的 AR_Panel Prefab 拖到這裡")]
    public GameObject arPanelPrefab;   

    [Header("Environment Settings")]
    [Tooltip("電腦測試用的 Webcam 背景 Canvas（HoloLens 上會自動關閉）")]
    public GameObject webcamCanvas; 

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
        public Vector3 targetPosition;
        public Vector3 targetLocalScale; // 🌟 真正隨距離變化的動態 Scale
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

        barcodeReader = new BarcodeReader
        {
            AutoRotate = true,
            Options = new ZXing.Common.DecodingOptions
            {
                TryHarder = true,
                PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE }
            }
        };

        #if UNITY_EDITOR
        if (webcamCanvas != null) webcamCanvas.SetActive(true);
        #else
        if (webcamCanvas != null) webcamCanvas.SetActive(false);
        #endif

        StartCoroutine(ScanQRRoutine());
    }

    void Update()
    {
        List<string> keysToRemove = new List<string>();

        foreach (var kvp in activePanels)
        {
            string qrKey = kvp.Key;
            ActivePanel panel = kvp.Value;

            if (Time.time - panel.lastSeenTime > hideDelay)
            {
                if (panel.panelInstance != null) Destroy(panel.panelInstance);
                keysToRemove.Add(qrKey);
                continue;
            }

            if (panel.panelInstance == null) continue;

            if (!panel.panelInstance.activeSelf)
            {
                panel.panelInstance.SetActive(true);
            }

            #if UNITY_EDITOR
            if (panel.panelInstance.transform.parent != mainCameraTransform)
            {
                panel.panelInstance.transform.SetParent(mainCameraTransform, true);
            }

            panel.panelInstance.transform.localPosition = Vector3.Lerp(
                panel.panelInstance.transform.localPosition, 
                panel.targetPosition, 
                Time.deltaTime * 12f
            );

            panel.panelInstance.transform.localRotation = Quaternion.Euler(0f, 0f, 0f);

            panel.panelInstance.transform.localScale = Vector3.Lerp(
                panel.panelInstance.transform.localScale, 
                panel.targetLocalScale, 
                Time.deltaTime * 12f
            );
            #else
            if (panel.panelInstance.transform.parent != null)
            {
                panel.panelInstance.transform.SetParent(null);
            }

            // 平滑跟隨位置
            panel.panelInstance.transform.position = Vector3.Lerp(
                panel.panelInstance.transform.position, 
                panel.targetPosition, 
                Time.deltaTime * 12f
            );

            // 面板始終朝向使用者眼睛
            if (mainCameraTransform != null)
            {
                panel.panelInstance.transform.LookAt(mainCameraTransform.position);
                panel.panelInstance.transform.Rotate(0, 180, 0);
            }

            // 🌟 關鍵修正：平滑跟隨「動態比例」，手機拿近時面板會即時放大！
            panel.panelInstance.transform.localScale = Vector3.Lerp(
                panel.panelInstance.transform.localScale, 
                panel.targetLocalScale, 
                Time.deltaTime * 12f
            );
            #endif
        }

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

        // 1. 計算 QR Code 中心點
        float sumX = 0, sumY = 0;
        foreach (var p in points)
        {
            sumX += p.X;
            sumY += p.Y;
        }
        float centerX = sumX / points.Length;
        float centerY = sumY / points.Length;

        // 2. 計算 QR Code 在相機畫面中的「真實像素大小」
        float qrSizeInPixels = 100f; 
        if (points.Length >= 2)
        {
            float maxDist = 0f;
            for (int i = 0; i < points.Length; i++)
            {
                for (int j = i + 1; j < points.Length; j++)
                {
                    float dist = Vector2.Distance(new Vector2(points[i].X, points[i].Y), new Vector2(points[j].X, points[j].Y));
                    if (dist > maxDist) maxDist = dist;
                }
            }
            qrSizeInPixels = maxDist / 1.414f; 
        }

        Vector3 calculatedPos;
        Vector3 calculatedScale;

        #if UNITY_EDITOR
        float realWidth = cameraFeed.cam != null ? cameraFeed.cam.width : camWidth;
        float realHeight = cameraFeed.cam != null ? cameraFeed.cam.height : camHeight;

        float offsetX = (centerX / realWidth) - 0.5f;
        float offsetY = 0.5f - (centerY / realHeight); 

        if (mainCameraTransform != null)
        {
            calculatedPos = mainCameraTransform.position 
                          + mainCameraTransform.forward * 0.5f 
                          + mainCameraTransform.right * (offsetX * 0.4f) 
                          + mainCameraTransform.up * (offsetY * 0.4f + 0.02f);
        }
        else
        {
            calculatedPos = new Vector3(offsetX, offsetY, 0.5f);
        }

        // Editor 動態 Scale
        float editorDynamic = Mathf.Clamp(qrSizeInPixels * 0.00001f, 0.0005f, 0.002f);
        calculatedScale = new Vector3(editorDynamic, editorDynamic, editorDynamic);

        #else
        // 👓 【HoloLens 2 實機真正的動態隨距離變化邏輯】
        if (mainCameraTransform != null)
        {
            float normX = (centerX / (float)camWidth) - 0.5f;
            float normY = 0.5f - (centerY / (float)camHeight); 

            // 根據像素估算距離 (0.3m ~ 1.0m)
            float distance = Mathf.Clamp(260f / qrSizeInPixels, 0.3f, 1.0f);

            Vector3 centerWorldPos = mainCameraTransform.position 
                                   + mainCameraTransform.forward * distance 
                                   + mainCameraTransform.right * (normX * distance * 0.7f) 
                                   + mainCameraTransform.up * (normY * distance * 0.7f);

            // 位置始終貼在 QR Code 頭頂
            calculatedPos = centerWorldPos + mainCameraTransform.up * 0.02f;
        }
        else
        {
            calculatedPos = Vector3.forward * 0.5f;
        }

        // 🌟 🌟 關鍵邏輯：Scale 徹底跟隨 qrSizeInPixels 動態計算！🌟 🌟
        // 拿極遠(50px) -> Scale = 0.0003 (微型標籤)
        // 拿極近(300px) -> Scale = 0.0018 (隨手機放大 6 倍，極度清晰且比例完美)
        float dynamicScale = Mathf.Clamp(qrSizeInPixels * 0.000006f, 0.0003f, 0.002f);
        calculatedScale = new Vector3(dynamicScale, dynamicScale, dynamicScale);
        #endif

        // 3. 更新或新建面板
        if (activePanels.TryGetValue(qrKey, out ActivePanel existingPanel))
        {
            existingPanel.targetPosition = calculatedPos;
            existingPanel.targetLocalScale = calculatedScale; // 🌟 實時傳遞最新的動態尺寸
            existingPanel.lastSeenTime = Time.time;
            
            UpdateTextDisplay(existingPanel.titleText, existingPanel.statusText, seatId, status);
        }
        else
        {
            GameObject newPanelObj = Instantiate(arPanelPrefab);
            newPanelObj.SetActive(true);

            TMP_Text titleComponent = newPanelObj.transform.Find("Title")?.GetComponent<TMP_Text>();
            TMP_Text statusComponent = newPanelObj.transform.Find("Status")?.GetComponent<TMP_Text>();

            if (titleComponent == null || statusComponent == null)
            {
                TMP_Text[] tmps = newPanelObj.GetComponentsInChildren<TMP_Text>();
                if (tmps.Length >= 2)
                {
                    titleComponent = tmps[0];
                    statusComponent = tmps[1];
                }
            }

            if (Application.isEditor)
            {
                newPanelObj.transform.localPosition = calculatedPos;
            }
            else
            {
                newPanelObj.transform.position = calculatedPos;
            }
            
            newPanelObj.transform.localScale = calculatedScale;

            UpdateTextDisplay(titleComponent, statusComponent, seatId, status);

            ActivePanel newPanel = new ActivePanel
            {
                panelInstance = newPanelObj,
                titleText = titleComponent,
                statusText = statusComponent,
                targetPosition = calculatedPos,
                targetLocalScale = calculatedScale,
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