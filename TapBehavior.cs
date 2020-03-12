using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using HoloToolkit.Unity.InputModule;
using System;
using System.IO;
using System.Linq;
using UnityEngine.XR.WSA.WebCam;
using UnityEngine.Rendering;
using HoloToolkit.Unity.SpatialMapping;

public class TapBehavior : MonoBehaviour, IInputClickHandler
{
    #region Definition
    //テスト用（できてたら消す）
    //Queue<AsyncGPUReadbackRequest> _requests = new Queue<AsyncGPUReadbackRequest>();
    //public Texture2D srcTexture;

    private PhotoCapture photoCaptureObject = null;
    //private Material changeMaterial = null;

    //投影変換用
    [System.NonSerialized]
    public List<Matrix4x4> projectionMatrixList;
    [System.NonSerialized]
    public List<Matrix4x4> worldToCameraMatrixList;

    //srcTexture保存用
    //byte[] bytes;
    //private int currentPhoto = 0;

    //private int[,] position;

    //K-means法用============================================
    public ComputeShader imgCS;
    private RenderTexture testTex;
    private static int cluster_size = 7;//クラスター数
    Color[] centroid = new Color[cluster_size];//重心用
    Color[] buffer = new Color[cluster_size];//一つ前の重心

    public GameObject obj;//光源推定のための目印用(Instantiate(obj, 場所、角度)で配置)
    public GameObject shadowobj;
    //カメラ座標
    private Vector3 camerapos;
    #endregion Definition

    // Use this for initialization
    void Start()
    {
        // AirTap時のイベントを設定する
        InputManager.Instance.PushFallbackInputHandler(gameObject);

        //Listの初期化
        projectionMatrixList = new List<Matrix4x4>();
        worldToCameraMatrixList = new List<Matrix4x4>();
    }

    //オブジェクトの保存・写真モード開始
    void OnPhotoCaptureCreated(PhotoCapture captureObject)
    {
        photoCaptureObject = captureObject;

        Resolution cameraResolution = PhotoCapture.SupportedResolutions.OrderByDescending((res) => res.width * res.height).First();

        CameraParameters c = new CameraParameters();
        c.hologramOpacity = 0.0f;
        c.cameraResolutionWidth = cameraResolution.width;
        c.cameraResolutionHeight = cameraResolution.height;
        c.pixelFormat = CapturePixelFormat.BGRA32;

        captureObject.StartPhotoModeAsync(c, OnPhotoModeStarted);
    }

    //クリーンアップ
    void OnStoppedPhotoMode(PhotoCapture.PhotoCaptureResult result)
    {
        photoCaptureObject.Dispose();
        photoCaptureObject = null;
    }

    private void OnPhotoModeStarted(PhotoCapture.PhotoCaptureResult result)
    {
        if (result.success)
        {
            photoCaptureObject.TakePhotoAsync(OnCapturedPhotoToMemory);
        }
    }

    //Texture2D
    void OnCapturedPhotoToMemory(PhotoCapture.PhotoCaptureResult result, PhotoCaptureFrame photoCaptureFrame)
    {
        if (result.success)
        {
            // 使用するTexture2Dを作成し、正しい解像度を設定する
            Resolution cameraResolution = PhotoCapture.SupportedResolutions.OrderByDescending((res) => res.width * res.height).First();
            Texture2D srcTexture = new Texture2D(cameraResolution.width, cameraResolution.height);
            // 画像データをターゲットテクスチャにコピーする(画像データがsrcTextureに入ってる)
            photoCaptureFrame.UploadImageDataToTexture(srcTexture);

            //カメラの座標を記録
            camerapos = Camera.main.transform.position;
            //カメラの方向
            Vector3 gazeDirection = Camera.main.transform.forward;
            //試しで入れてみる（TakePictureから引用）
            Matrix4x4 cameraToWorldMatrix;
            photoCaptureFrame.TryGetCameraToWorldMatrix(out cameraToWorldMatrix);
            Matrix4x4 worldToCameraMatrix = cameraToWorldMatrix.inverse;

            //位置データが利用可能な場合、写真がキャプチャされた時点の投影行列を返します。
            Matrix4x4 projectionMatrix;
            photoCaptureFrame.TryGetProjectionMatrix(out projectionMatrix);
            projectionMatrixList.Add(projectionMatrix);
            worldToCameraMatrixList.Add(worldToCameraMatrix);

            //Kmeans用
            Texture2D resultTexture = new Texture2D(cameraResolution.width, cameraResolution.height);

            //平滑化
            resultTexture = ImageProgress(srcTexture, photoCaptureFrame, cameraResolution);

            //もし光源あったら
            bool lightjudg = false;
            int[,] map = new int[resultTexture.width, resultTexture.height];
            int pos_x = 0, pos_y = 0;
            for (int w = 0; w < resultTexture.width; w++)
            {
                for (int h = 0; h < resultTexture.height; h++)
                {
                    Color lig = resultTexture.GetPixel(w, h);
                    if (lig.r == 1 && lig.g == 0 && lig.b == 1)
                    {
                        //座標を記録
                        lightjudg = true;
                        pos_x = w;
                        pos_y = h;
                    }
                }
            }

            if (lightjudg == true)
            {
                Debug.Log(pos_x);
                Debug.Log(pos_y);
                //新しいクラス（その座標を空間座標へ)
                OnPhotoCaptured(resultTexture, pos_x, pos_y, photoCaptureFrame, cameraToWorldMatrix, projectionMatrix, camerapos);

            }
            else if (lightjudg == false)
            {
                //もし画素値が0.9以下ならkmeans法
                Needclasster(resultTexture, map);
            }
        }
        // クリーンアップ
        photoCaptureObject.StopPhotoModeAsync(OnStoppedPhotoMode);
    }

    #region Kmeans

    #region HeikatuKa
    //RenderTextureをTexture２Dに変換
    static Texture2D CreateTexture2D(RenderTexture rt)
    {
        //Texture2Dを作成
        Texture2D texture2D = new Texture2D(rt.width, rt.height, TextureFormat.ARGB32, false, false);
        RenderTexture.active = rt;
        texture2D.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        texture2D.Apply();

        //元に戻す別のカメラを用意してそれをRenderTexter用にすれば下のコードはいらないです。
        RenderTexture.active = null;
        return texture2D;
    }

    //平滑化処理
    Texture2D ImageProgress(Texture2D src, PhotoCaptureFrame photoCaptureFrame, Resolution camera)
    {
        RenderTexture resultTex;
        resultTex = new RenderTexture(camera.width, camera.height, 0, RenderTextureFormat.ARGB32);
        resultTex.enableRandomWrite = true;
        resultTex.Create();

        var step1 = imgCS.FindKernel("imageprogress");
        imgCS.SetTexture(step1, "srcTexture", src);
        imgCS.SetTexture(step1, "Result", resultTex);
        imgCS.Dispatch(step1, src.width / 4, src.height / 4, 1);

        Texture2D texture = CreateTexture2D(resultTex);
        return texture;
    }

    #endregion HeikatuKa

    #region km
    //初期値
    private void Randinit()
    {
        for (int i = 0; i < cluster_size; i++)
        {
            centroid[i] = new Color(0.14285f * i, 0.14285f * i, 0.14285f * i, 1);
        }
    }

    //重心が停止してるか判断
    private bool ClusterCheck()
    {
        int cnt = 0;
        bool ret = false;

        for (int i = 0; i < cluster_size; i++)
        {
            float rab = Math.Abs(centroid[i].r - buffer[i].r);
            float gab = Math.Abs(centroid[i].g - buffer[i].g);
            float bab = Math.Abs(centroid[i].b - buffer[i].b);

            if (rab + gab + bab < 0.05)//centroid(今の重心)とbuffer(一つ前の重心)のRGB値が同じなら
            {
                cnt = cnt + 1;
            }
        }
        if (cnt == cluster_size)
        {
            ret = true;
        }
        else
        {
            ret = false;
        }

        return ret;
    }

    //画素（Point）と重心の色の距離を計算
    private float ColorDistance(int ax, int ay, Color b, Texture2D Image)
    {
        float dR = Image.GetPixel(ax, ay).r - b.r;
        float dG = Image.GetPixel(ax, ay).g - b.g;
        float dB = Image.GetPixel(ax, ay).b - b.b;

        return dR * dR + dG * dG + dB * dB;
    }

    //kmeans法を行う
    void Needclasster(Texture2D ProgressedImage, int[,] mapp)
    {
        Randinit();

        while (ClusterCheck() == false)
        {
            for (int i = 0; i < ProgressedImage.width; i++)
            {
                for (int j = 0; j < ProgressedImage.height; j++)
                {
                    double dist = 0.005;
                    double neardistribution = 0.07;//近いと判定する距離
                    int place = 0;

                    for (int k = 0; k < cluster_size; k++)
                    {
                        dist = ColorDistance(i, j, centroid[k], ProgressedImage);//距離計算ColorDistanceはShaderごり押し
                        if (dist < neardistribution)
                        {
                            neardistribution = dist;
                            place = k;//クラスターを決定
                        }
                    }
                    mapp[i, j] = place;
                }
            }

            //重心を計算
            float[] sum_R = new float[cluster_size];
            float[] sum_G = new float[cluster_size];
            float[] sum_B = new float[cluster_size];
            int[] num = new int[cluster_size];
            // 重心を計算
            for (int i = 0; i < ProgressedImage.width; i++)
            {
                for (int j = 0; j < ProgressedImage.height; j++)
                {
                    sum_R[mapp[i, j]] += ProgressedImage.GetPixel(i, j).r;
                    sum_G[mapp[i, j]] += ProgressedImage.GetPixel(i, j).g;
                    sum_B[mapp[i, j]] += ProgressedImage.GetPixel(i, j).b;
                    num[mapp[i, j]] = num[mapp[i, j]] + 1;
                }
            }

            for (int k = 0; k < cluster_size; k++)
            {
                // 前の重心位置を記憶しておく
                buffer[k] = new Color(centroid[k].r, centroid[k].g, centroid[k].b);

                if (num[k] == 0)//0なら以下の作業をスキップする
                {
                    continue;
                }

                // 重心位置の更新
                centroid[k] = new Color(sum_R[k] / num[k], sum_G[k] / num[k], sum_B[k] / num[k]);
            }
        }
        //以下表示用
        Texture2D resltimage = new Texture2D(ProgressedImage.width, ProgressedImage.height);
        for (int i = 0; i < ProgressedImage.width; i++)
        {
            for (int j = 0; j < ProgressedImage.height; j++)
            {
                resltimage.SetPixel(i, j, centroid[mapp[i, j]]);
            }
        }
        resltimage.Apply();
        GetComponent<Renderer>().material.mainTexture = resltimage;
    }
    #endregion km

    #endregion Kmeans

    #region LightPosition


    void OnPhotoCaptured(Texture2D image, int x, int y, PhotoCaptureFrame photoCaptureFrame, Matrix4x4 cameraToWorldMatrix, Matrix4x4 projectionMatrix, Vector3 cameraposition)
    {
        GetComponent<Renderer>().material.mainTexture = image;
        photoCaptureObject.StopPhotoModeAsync(OnStoppedPhotoMode);

        //y座標はテクスチャ上では反転しているから1から引く
        var imagePosZeroToOne = new Vector2((float)x / (float)image.width, (float)1 - ((float)y / (float)image.height));
        var imagePosProjected = (imagePosZeroToOne * 2) - new Vector2(1, 1);    // テクスチャ上の座標をー1から１の範囲にする
        Debug.Log(image.width);
        Debug.Log(image.height);
        var cameraSpacePos = UnProjectVector(projectionMatrix, new Vector3(imagePosProjected.x, imagePosProjected.y, 1));//元座標
        var worldSpaceCameraPos = cameraToWorldMatrix.MultiplyPoint(Vector3.zero);     // camera location in world space
        var worldSpaceBoxPos = cameraToWorldMatrix.MultiplyPoint(cameraSpacePos);   // ray point in world space

        var CameraToBox = worldSpaceBoxPos - worldSpaceCameraPos;

        RaycastHit hit;
        bool hitToMap = Physics.Raycast(worldSpaceCameraPos, worldSpaceBoxPos - worldSpaceCameraPos, out hit, 30, SpatialMappingManager.Instance.LayerMask);

        var objpos = shadowobj.transform.position;
        if (hitToMap == true)
        {
            Debug.Log("ライト設置！");
            GameObject lightGameObject = new GameObject("The Light");
            Light lightComp = lightGameObject.AddComponent<Light>();
            lightComp.color = Color.white;
            lightComp.transform.position = cameraposition + CameraToBox;
            lightComp.transform.forward = objpos - lightComp.transform.position;
            Instantiate(obj, hit.point, new Quaternion(0, 0, 0, 0));
            Instantiate(obj, worldSpaceCameraPos, new Quaternion(0, 0, 0, 0));
        }
    }

    public static Vector3 UnProjectVector(Matrix4x4 proj, Vector3 to)
    {
        Vector3 from = new Vector3(0, 0, 0);
        var axsX = proj.GetRow(0);
        var axsY = proj.GetRow(1);
        var axsZ = proj.GetRow(2);
        from.z = to.z / axsZ.z;　//割合？
        from.y = (to.y - (from.z * axsY.z)) / axsY.y;
        from.x = (to.x - (from.z * axsX.z)) / axsX.x;
        return from;
    }


    #endregion LightPosition


    //写真保存用
    private string PictureFileDirectoryPath()
    {
        string directorypath = "";
#if WINDOWS_UWP
    // HoloLens上での動作の場合、LocalAppData/AppName/LocalStateフォルダを参照する
    directorypath = Windows.Storage.ApplicationData.Current.LocalFolder.Path;
#else
        // Unity上での動作の場合、Assets/StreamingAssetsフォルダを参照する
        directorypath = UnityEngine.Application.streamingAssetsPath;
#endif
        return directorypath;
    }

    private void OnDestroy()
    {

    }

    /// <summary>
    /// クリックイベント
    /// </summary>
    public void OnInputClicked(InputClickedEventData eventData)
    {
        // キャプチャを開始する
        PhotoCapture.CreateAsync(true, OnPhotoCaptureCreated);
    }

    // Update is called once per frame
    void Update()
    {
    }
}