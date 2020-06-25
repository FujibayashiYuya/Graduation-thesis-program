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

public class Histglam_test : MonoBehaviour, IInputClickHandler
{
    // Start is called before the first frame update
    private PhotoCapture photoCaptureObject = null;
    void Start()
    {
        // AirTap時のイベントを設定する
        InputManager.Instance.PushFallbackInputHandler(gameObject);
    }
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

    private void OnPhotoModeStarted(PhotoCapture.PhotoCaptureResult result)
    {
        if (result.success)
        {
            photoCaptureObject.TakePhotoAsync(OnCapturedPhotoToMemory);
        }
        else
        {
            Debug.LogError("Unable to start photo mode!");
        }
    }

    public void OnInputClicked(InputClickedEventData eventData)
    {
        //AirTap検出時の処理を記述
        Catchnormal();
    }

    void OnCapturedPhotoToMemory(PhotoCapture.PhotoCaptureResult result, PhotoCaptureFrame photoCaptureFrame)
    {
        if (result.success)
        {
            // 使用するTexture2Dを作成し、正しい解像度を設定する
            // Create our Texture2D for use and set the correct resolution
            Resolution cameraResolution = PhotoCapture.SupportedResolutions.OrderByDescending((res) => res.width * res.height).First();
            Texture2D targetTexture = new Texture2D(cameraResolution.width, cameraResolution.height);
            // 画像データをターゲットテクスチャにコピーする
            // Copy the raw image data into our target texture
            photoCaptureFrame.UploadImageDataToTexture(targetTexture);
            // マテリアルに適用するなど、テクスチャで行いたい操作を実施します
            // Do as we wish with the texture such as apply it to a material, etc.
        }
        // クリーンアップ
        // Clean up
        photoCaptureObject.StopPhotoModeAsync(OnStoppedPhotoMode);
    }


    void Catchnormal() {
        RaycastHit hit;
        var headPos = Camera.main.transform.position;
        var gazeDirection = Camera.main.transform.forward;
        bool hitToMap = Physics.Raycast(headPos, gazeDirection, out hit, 30, SpatialMappingManager.Instance.LayerMask);

        if (hitToMap)
        {
            Vector3 raynormal = hit.normal;//法線は少数第1位まで出る
            float nx = raynormal.x * 10 + 10;
            float ny = raynormal.y * 10 + 10;
            float nz = raynormal.z * 10 + 10;
            int[,,] normalnum = new int[25,25,25];
            normalnum[(int)nx, (int)ny, (int)nz]++;//同じベクトルの個数を求める（範囲は-1～1を０～２に変換している）
            Debug.Log("法線ベクトルは" + raynormal);
        }
    }

    public static Vector2 VecToPolTr(double x, double y, double z)//xyzをθφに変換し返す関数
    {
        double r = Math.Sqrt(x * x + y * y + z * z);
        double r2d = Math.Sqrt(x * x + z * z);
        double theta = Math.Acos(y / r);
        double phi = Math.Acos(x / r2d);
        Vector2 mappos = new Vector2((float)theta, (float)phi);
        return mappos;
    }
    void OnStoppedPhotoMode(PhotoCapture.PhotoCaptureResult result)
    {
        photoCaptureObject.Dispose();
        photoCaptureObject = null;
    }


}
