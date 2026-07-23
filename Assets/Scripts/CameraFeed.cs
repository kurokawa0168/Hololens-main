using System.Collections;
using UnityEngine;
using UnityEngine.UI; // 🌟 引入 UI 命名空間

public class CameraFeed : MonoBehaviour
{
    [HideInInspector]
    public WebCamTexture cam;

    // 🌟 新增：用來在電腦上顯示畫面的 RawImage 欄位
    public RawImage displayScreen; 

    void Start()
    {
        #if UNITY_EDITOR
        cam = new WebCamTexture(); 
        Debug.Log("💻 已在電腦模擬環境啟動預設 Webcam");
        #else
        cam = new WebCamTexture(896, 504, 30); 
        Debug.Log("👓 已在 HoloLens 2 啟動相機");
        #endif

        if (cam != null)
        {
            cam.Play();

            // 🌟 關鍵：如果在電腦上，且有拖入顯示面板，就把相機畫面秀在畫面上！
            if (displayScreen != null)
            {
                displayScreen.texture = cam;
            }
        }
        else
        {
            Debug.LogError("❌ 找不到可用的相機裝置！");
        }
        
        StartCoroutine(StreamFrameToBackend());
    }

    IEnumerator StreamFrameToBackend()
    {
        while (true)
        {
            if (cam != null && cam.isPlaying && cam.width > 100)
            {
                yield return new WaitForEndOfFrame();
                
                Texture2D frame = new Texture2D(cam.width, cam.height, TextureFormat.RGBA32, false);
                frame.SetPixels(cam.GetPixels());
                frame.Apply();

                byte[] imageBytes = frame.EncodeToJPG(50);
                Destroy(frame); 

                // 🌐 未來傳輸套件...
            }
            yield return new WaitForSeconds(0.1f);
        }
    }
}