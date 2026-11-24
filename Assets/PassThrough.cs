using Meta.XR;
using Meta.XR.Samples;
using Oculus.Platform.Models;
using PassthroughCameraSamples.MultiObjectDetection;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.UIElements;

public class PassThrough : MonoBehaviour
{
    [SerializeField] private PassthroughCameraAccess m_cameraAccess;
    [SerializeField] private SentisInferenceUiManager m_uiInference;
    private Texture2D m_cameraSnapshot;
    [SerializeField] private RawImage m_image;
    [SerializeField] private GameObject imagePrefab;
    [SerializeField] private GameObject parentCanvas;
    [SerializeField] private Camera m_camera;

    private Transform controller;
    private IEnumerator Start()
    {

        controller = GameObject.Find("RightControllerAnchor").transform;
        /*
        m_rayGo1 = m_rayMarker;
        m_rayGo2 = Instantiate(m_rayMarker);
        m_rayGo3 = Instantiate(m_rayMarker);
        m_rayGo4 = Instantiate(m_rayMarker);
        */

        if (m_cameraAccess == null)
        {
            //Debug.LogError($"PCA: {nameof(m_cameraAccess)} field is required "
            //            + $"for the component {nameof(CameraToWorldManager)} to operate properly");
            enabled = false;
            yield break;
        }

        Assert.IsTrue(m_cameraAccess.enabled, "m_cameraAccess.enabled");
        while (!m_cameraAccess.IsPlaying)
        {
            yield return null;
        }

        //ScaleCameraCanvas();
        //UpdateRaysRendering();
    }
    public IEnumerator TakeSnapshot(Rect currentBox,Vector3 bottomRightLocal,Vector3 topLeftLocal, Vector2 center)
    {
        yield return new WaitForEndOfFrame();
        if (m_cameraAccess.IsPlaying)
        {
            m_cameraAccess.GetTexture();
        }
        var size = m_cameraAccess.CurrentResolution;
        //Vector2 ratio = new Vector2((float)m_camera.pixelWidth / (float)size.x, (float)m_camera.pixelHeight / (float)size.y);
        //Debug.Log("size x and pixel width: " + (float)size.x / (float)m_camera.pixelWidth);
        //Debug.Log("size x: " + size.x + "pixel witdt: " + m_camera.pixelWidth);
        
        if (m_cameraSnapshot == null)
        {
            //Debug.Log("Camera current resolution is: " + m_cameraAccess.CurrentResolution);
            //Debug.Log("Unity Camera resolution is: " + m_camera.pixelHeight + " " + m_camera.pixelWidth);
            m_cameraSnapshot = new Texture2D(size.x, size.y, TextureFormat.RGBA32, false); //behold den her
            //int x = Mathf.FloorToInt(currentBox.width);
            //int y = Mathf.FloorToInt(currentBox.height);
            //m_cameraSnapshot = new Texture2D(x, y, TextureFormat.RGBA32, false); //Needs testing
        }

        //int localx = Mathf.FloorToInt(localPos.x);
        //int localy = Mathf.FloorToInt(localPos.y);
        //Rect regionToReadFrom = new Rect(0, 0, currentBox.width, currentBox.height);
        //Debug.Log(regionToReadFrom + " " + x + " " + y);

        //m_cameraSnapshot.ReadPixels(regionToReadFrom, 0, 0); //Needs testing if works. - Test with LoadRawTextureData
        
        var currentImage = Instantiate(imagePrefab, parentCanvas.transform);
        currentImage.transform.position = controller.position;

        m_cameraSnapshot.LoadRawTextureData(m_cameraAccess.GetColors()); //det her virker også fint, den burde give mig et 1280 x 960 resolution texture

        //Debug.Log("Placement of images: " + Mathf.FloorToInt(center.x) + Mathf.FloorToInt(center.y));
        //Debug.Log("Bottom right local: " + bottomRightLocal + "Top Left Local: " + topLeftLocal);
        //Debug.Log("World To Screen Point BR: " + m_camera.WorldToScreenPoint(bottomRightLocal));
        //Debug.Log("World To Screen Point TL: " + m_camera.WorldToScreenPoint(topLeftLocal));
        //Debug.Log("currentbox Rect: " + Mathf.FloorToInt(currentBox.width) + Mathf.FloorToInt(currentBox.height));

        //Vector3 topLeft = m_camera.WorldToScreenPoint(topLeftLocal);
        //Vector3 bottomRight = m_camera.WorldToScreenPoint(bottomRightLocal);

        //int width = Mathf.FloorToInt((topLeft.x - bottomRight.x) / ratio.x);
        //int height = Mathf.FloorToInt((topLeft.y - bottomRight.y) / ratio.y);

        //Debug.Log("What are the ratios? " + ratio.x + " " + ratio.y);

        //Debug.Log("What are the width and height? " + width + " " + height);
        //Debug.Log("Left Cornor: " + topLeft.x / ratio.x + " Right Cornor: " + bottomRight.y / ratio.y);

        //Behold metoden under, men erstat de to første med bottom left som anchor punkt
        //Prøv at tage fat i den rect fra SentisUIManager, find ud af hvilket x1 x2 y1 y2 er bottom left
        //Debug den Rect
        Debug.Log("Current box: " + currentBox);
        Color[] pixels = m_cameraSnapshot.GetPixels(Mathf.FloorToInt(currentBox.x * size.x), Mathf.FloorToInt(currentBox.y * size.y), Mathf.FloorToInt(currentBox.width * size.x), Mathf.FloorToInt(currentBox.height * size.y));

        Texture2D result = new Texture2D(Mathf.FloorToInt(currentBox.width * size.x), Mathf.FloorToInt(currentBox.height * size.y));
        result.SetPixels(pixels);
        result.Apply();
        //m_cameraSnapshot.LoadRawTextureData(m_cameraAccess.GetTexture()); //Test this, see what it does
        m_cameraSnapshot.Apply();
        var bytes = result.EncodeToPNG();
        //File.WriteAllBytes(Application.dataPath + "/../SavedScreen.png", bytes);
        m_image.texture = result;
        currentImage.GetComponent<RawImage>().texture = result;
        /*
        Normalize Yolo coordinates
        Yolo coordinates 
        "Center" coordinates passer med de passthrough resolution af cameraet - brug normalized som er
        */
    }
}
