using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

// 定義與後端傳回來的 JSON 對應的資料結構
[Serializable]
public class SeatResponse
{
    public bool success;
    public string seatId;
    public string status; // 後端傳回來的狀態 (例如: "Available", "Occupied", "Green", "Red")
}

public class SeatDataFetcher : MonoBehaviour
{
    [Header("後端 API 設定")]
    [Tooltip("請填入你現有後端的完整網址 (包含 Port)，例如 http://192.168.1.100:3000/api/seat/")]
    public string apiBaseUrl = "http://YOUR_BACKEND_IP:3000/api/seat/";

    /// <summary>
    /// 發送 GET 請求向後端查詢座位資料
    /// </summary>
    public IEnumerator GetSeatStatus(string seatId, Action<string> callback)
    {
        string requestUrl = apiBaseUrl + seatId;

        using (UnityWebRequest request = UnityWebRequest.Get(requestUrl))
        {
            // 設定 3 秒超時，避免後端沒回應時卡死
            request.timeout = 3;

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    // 解析後端傳回的 JSON 格式
                    SeatResponse response = JsonUtility.FromJson<SeatResponse>(request.downloadHandler.text);
                    if (response != null && !string.IsNullOrEmpty(response.status))
                    {
                        callback?.Invoke(response.status);
                    }
                    else
                    {
                        callback?.Invoke("Unknown");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("JSON 解析失敗: " + ex.Message);
                    callback?.Invoke("Unknown");
                }
            }
            else
            {
                Debug.LogWarning($"後端請求失敗 [{request.error}]: {requestUrl}");
                callback?.Invoke("Unknown");
            }
        }
    }
}