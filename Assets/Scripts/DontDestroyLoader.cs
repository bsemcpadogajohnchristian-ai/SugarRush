using UnityEngine;

public class DontDestroyLoader : MonoBehaviour
{
    private static DontDestroyLoader _instance;

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        DontDestroyOnLoad(gameObject);
    }
}
