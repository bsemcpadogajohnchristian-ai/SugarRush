// DontDestroyLoader.cs
// Sugar Rush
// Unity 6.3 LTS + Netcode for GameObjects v2.1+
//
// Attach this to the root GameObject that holds your NetworkManager.
// Keeps NetworkManager alive across scene loads.
// Singleton guard prevents duplicates when returning to LobbyScene.

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
