using UnityEngine;

public class GameManager : MonoBehaviour
{
    private void Awake()
    {
        if (Application.isMobilePlatform) Application.targetFrameRate = 120;
        else Application.targetFrameRate = -1;
    }
}
