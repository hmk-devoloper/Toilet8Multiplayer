using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Globalization;

[BepInPlugin("com.toilet8.socketmultiplayer", "Toilet8 Socket Multiplayer", "1.0.0")]
public class Toilet8MultiplayerMod : BaseUnityPlugin
{
    private UdpClient udpClient;
    private IPEndPoint remoteEndPoint;
    private bool isHost = false;
    private bool isConnected = false;
    private Thread receiveThread;
    private Vector3 lastSentPosition;
    private float sendInterval = 0.05f;
    private float sendTimer = 0f;
    private GameObject playerObj;
    private string serverIP = "127.0.0.1";
    private int port = 7777;

    private Dictionary<string, GameObject> remotePlayers = new Dictionary<string, GameObject>();
    private string localIP = "127.0.0.1";
    private string logFilePath;
    private bool networkStarted = false;
    private GameObject cubePrefab;

    private void Awake()
    {
        Harmony harmony = new Harmony("com.toilet8.socketmultiplayer");
        harmony.PatchAll();

        logFilePath = Path.Combine(Paths.GameRootPath, "multiplayer.log");
        File.WriteAllText(logFilePath, "");
        Log("=== Toilet8 Multiplayer Mod ===");
        Log($"Log file: {logFilePath}");

        CreateCubePrefab();

        localIP = GetLocalIPAddress();
        Log($"Local IP: {localIP}");

        string hostFlagPath = Path.Combine(Paths.GameRootPath, "multiplayer.host");
        if (File.Exists(hostFlagPath))
        {
            isHost = true;
            Log("HOST MODE");
        }
        else
        {
            isHost = false;
            string serverFilePath = Path.Combine(Paths.GameRootPath, "multiplayer.server");
            if (File.Exists(serverFilePath))
            {
                serverIP = File.ReadAllText(serverFilePath).Trim();
                Log($"CLIENT MODE. Server IP: {serverIP}");
            }
            else
            {
                Log("CLIENT MODE. No server file, using localhost.");
            }
        }

        SceneManager.sceneLoaded += OnSceneLoaded;
        if (SceneManager.GetActiveScene().isLoaded)
            StartPlayerSearch();
    }

    private void CreateCubePrefab()
    {
        cubePrefab = new GameObject("CubePrefab");
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.transform.SetParent(cubePrefab.transform);
        cube.transform.localPosition = Vector3.zero;
        cube.transform.localScale = Vector3.one * 0.5f;

        Shader shader = Shader.Find("HDRP/Unlit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Color");
        if (shader == null) shader = Shader.Find("Standard");
        Material mat = new Material(shader);
        mat.color = Color.magenta;
        cube.GetComponent<Renderer>().material = mat;
        Destroy(cube.GetComponent<Collider>());

        var textObj = new GameObject("Nickname");
        textObj.transform.SetParent(cubePrefab.transform);
        textObj.transform.localPosition = new Vector3(0, 1.0f, 0);
        var textMesh = textObj.AddComponent<TextMesh>();
        textMesh.text = "Player";
        textMesh.fontSize = 24;
        textMesh.color = Color.white;
        textMesh.alignment = TextAlignment.Center;
        textMesh.anchor = TextAnchor.MiddleCenter;
        textMesh.characterSize = 0.1f;
        textMesh.fontStyle = FontStyle.Bold;

        cubePrefab.SetActive(false);
        DontDestroyOnLoad(cubePrefab);
    }

    private void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        Logger.LogInfo(msg);
        try { File.AppendAllText(logFilePath, line + Environment.NewLine); } catch { }
    }

    private string GetLocalIPAddress()
    {
        try
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
            {
                socket.Connect("8.8.8.8", 65530);
                IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                return endPoint?.Address.ToString() ?? "127.0.0.1";
            }
        }
        catch { return "127.0.0.1"; }
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        Log($"Scene loaded: {scene.name}");
        StartPlayerSearch();
    }

    private Coroutine playerSearchCoroutine;
    private void StartPlayerSearch()
    {
        if (playerSearchCoroutine != null)
            StopCoroutine(playerSearchCoroutine);
        playerSearchCoroutine = StartCoroutine(SearchForPlayer());
    }

    private IEnumerator SearchForPlayer()
    {
        while (playerObj == null)
        {
            playerObj = GameObject.Find("Player");
            if (playerObj != null)
            {
                Log("Player found!");
                CreateLocalPlayerMarker();
                if (!networkStarted)
                {
                    networkStarted = true;
                    if (isHost)
                        StartHost();
                    else
                        StartClient(serverIP, port);
                }
            }
            yield return new WaitForSeconds(0.5f);
        }
    }

    private void CreateLocalPlayerMarker()
    {
        if (playerObj == null) return;
        var marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        marker.transform.SetParent(playerObj.transform);
        marker.transform.localPosition = new Vector3(0, 2f, 0);
        marker.transform.localScale = Vector3.one * 0.3f;
        marker.GetComponent<Renderer>().material.color = Color.green;
        Destroy(marker.GetComponent<Collider>());
    }

    private void Update()
    {
        if (!isConnected || playerObj == null) return;

        sendTimer += Time.deltaTime;
        if (sendTimer >= sendInterval)
        {
            sendTimer = 0f;
            if (Vector3.Distance(playerObj.transform.position, lastSentPosition) > 0.01f)
            {
                lastSentPosition = playerObj.transform.position;
                SendPosition();
            }
        }
    }

    private void SendPosition()
    {
        if (udpClient == null) return;
        try
        {
            string message = string.Format(CultureInfo.InvariantCulture, "POS:{0},{1},{2}",
                playerObj.transform.position.x, playerObj.transform.position.y, playerObj.transform.position.z);
            byte[] data = Encoding.UTF8.GetBytes(message);

            if (isHost)
            {
                foreach (var kvp in remotePlayers)
                {
                    var ep = ParseEndPoint(kvp.Key);
                    if (ep != null)
                        udpClient.Send(data, data.Length, ep);
                }
            }
            else
            {
                if (remoteEndPoint != null)
                    udpClient.Send(data, data.Length, remoteEndPoint);
            }
        }
        catch (Exception ex)
        {
            Log($"Send error: {ex.Message}");
        }
    }

    private IPEndPoint ParseEndPoint(string key)
    {
        var parts = key.Split(':');
        if (parts.Length == 2 && IPAddress.TryParse(parts[0], out IPAddress ip) && int.TryParse(parts[1], out int p))
            return new IPEndPoint(ip, p);
        return null;
    }

    private void StartHost()
    {
        if (isConnected) return;
        try
        {
            udpClient = new UdpClient(port);
            receiveThread = new Thread(ReceiveLoop);
            receiveThread.IsBackground = true;
            receiveThread.Start();
            isHost = true;
            isConnected = true;
            Log($"Host started on {localIP}:{port}");
        }
        catch (Exception ex) { Log($"Host failed: {ex.Message}"); }
    }

    private void StartClient(string ip, int p)
    {
        if (isConnected) return;
        try
        {
            udpClient = new UdpClient();
            remoteEndPoint = new IPEndPoint(IPAddress.Parse(ip), p);
            byte[] data = Encoding.UTF8.GetBytes("HELLO");
            udpClient.Send(data, data.Length, remoteEndPoint);
            Log($"[SEND HELLO] to {remoteEndPoint}");

            receiveThread = new Thread(ReceiveLoop);
            receiveThread.IsBackground = true;
            receiveThread.Start();
            isHost = false;
            isConnected = true;
            serverIP = ip;
            port = p;
            Log($"Client connected to {ip}:{p}");
        }
        catch (Exception ex) { Log($"Connect failed: {ex.Message}"); }
    }

    private void StopConnection()
    {
        isConnected = false;
        receiveThread?.Join(500);
        udpClient?.Close();
        udpClient = null;
        isHost = false;

        UnityMainThreadDispatcher.Instance().Enqueue(() =>
        {
            foreach (var cube in remotePlayers.Values)
                Destroy(cube);
            remotePlayers.Clear();
        });
        Log("Disconnected.");
    }

    private void ReceiveLoop()
    {
        IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
        while (isConnected)
        {
            try
            {
                byte[] data = udpClient.Receive(ref sender);
                string message = Encoding.UTF8.GetString(data);
                ProcessMessage(message, sender);
            }
            catch (SocketException) { break; }
            catch (Exception ex) { Log($"Receive error: {ex.Message}"); }
        }
    }

    private void ProcessMessage(string message, IPEndPoint sender)
    {
        if (message.StartsWith("POS:"))
        {
            string[] parts = message.Substring(4).Split(',');
            if (parts.Length == 3)
            {
                if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    Vector3 newPos = new Vector3(x, y, z);
                    string senderKey = $"{sender.Address}:{sender.Port}";

                    UnityMainThreadDispatcher.Instance().Enqueue(() =>
                    {
                        if (isHost)
                        {
                            byte[] forwardData = Encoding.UTF8.GetBytes(message);
                            foreach (var kvp in remotePlayers)
                            {
                                if (kvp.Key != senderKey)
                                {
                                    var ep = ParseEndPoint(kvp.Key);
                                    if (ep != null)
                                        udpClient.Send(forwardData, forwardData.Length, ep);
                                }
                            }
                        }

                        if (remotePlayers.TryGetValue(senderKey, out GameObject cube))
                        {
                            cube.transform.position = newPos;
                        }
                        else
                        {
                            CreateRemoteCube(senderKey, newPos);
                        }
                    });
                }
            }
        }
        else if (message == "HELLO")
        {
            string senderKey = $"{sender.Address}:{sender.Port}";
            Log($"[RECV HELLO] from {senderKey}");
            UnityMainThreadDispatcher.Instance().Enqueue(() =>
            {
                if (!remotePlayers.ContainsKey(senderKey))
                    CreateRemoteCube(senderKey, Vector3.zero);
            });
        }
    }

    private void CreateRemoteCube(string senderKey, Vector3 position)
    {
        if (cubePrefab == null) return;
        var cube = Instantiate(cubePrefab);
        cube.name = $"RemotePlayer_{senderKey}";
        cube.transform.position = position;
        cube.SetActive(true);

        var textMesh = cube.GetComponentInChildren<TextMesh>();
        if (textMesh != null)
        {
            string[] ipParts = senderKey.Split(':')[0].Split('.');
            string shortName = ipParts.Length >= 4 ? ipParts[3] : senderKey;
            textMesh.text = $"Player {shortName}";
        }

        remotePlayers[senderKey] = cube;
        Log($"Cube created for {senderKey}, total players: {remotePlayers.Count}");
    }

    private void OnDestroy()
    {
        StopConnection();
    }

    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static UnityMainThreadDispatcher instance;
        private readonly Queue<Action> queue = new Queue<Action>();

        public static UnityMainThreadDispatcher Instance()
        {
            if (instance == null)
            {
                var go = new GameObject("MainThreadDispatcher");
                DontDestroyOnLoad(go);
                instance = go.AddComponent<UnityMainThreadDispatcher>();
            }
            return instance;
        }

        public void Enqueue(Action action)
        {
            lock (queue) { queue.Enqueue(action); }
        }

        private void Update()
        {
            lock (queue)
            {
                while (queue.Count > 0)
                    queue.Dequeue()?.Invoke();
            }
        }
    }
}
