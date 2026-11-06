using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Tasks.Vision.HandLandmarker;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using Mediapipe.Unity.Sample.HandLandmarkDetection;
using Mediapipe.Unity.Sample.PoseLandmarkDetection;

/// <summary>
/// Dibuja TODO en un solo lugar, con nodos conectados por líneas:
///  - Cuerpo: hombros y brazos (Pose)
///  - Hasta 2 manos (Hand)
///  - Cara: cejas, ojos y labios (Face)
/// Cuando HolaSignDetector.PoseCorrect es true, todos los nodos y líneas se pintan de verde.
/// Va en el objeto "PoseOverlay", hijo de "Annotatable Screen".
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class PoseDotsOverlay : MonoBehaviour
{
    [SerializeField, FormerlySerializedAs("hola"), Tooltip("Detector de la seña (verde = pose correcta). Si está vacío se busca solo.")] HolaSignDetector detectorHola;
    [SerializeField, FormerlySerializedAs("flipX"), Tooltip("Márcalo solo si los puntos se mueven al revés.")] bool invertirX = false;
    [SerializeField, FormerlySerializedAs("nodeSize"), Tooltip("Tamaño de los nodos del cuerpo y las manos.")] float tamanoNodo = 10f;
    [SerializeField, FormerlySerializedAs("lineWidth"), Tooltip("Grosor de las líneas del cuerpo y las manos.")] float grosorLinea = 4f;
    [SerializeField, FormerlySerializedAs("faceNodeSize"), Tooltip("Tamaño de los nodos de la cara (más fino).")] float tamanoNodoCara = 4f;
    [SerializeField, FormerlySerializedAs("faceLineWidth"), Tooltip("Grosor de las líneas de la cara.")] float grosorLineaCara = 2f;
    [SerializeField, FormerlySerializedAs("nodeColor"), Tooltip("Color de los nodos cuando falta algo (naranja).")] Color colorNodos = new Color(1f, 0.55f, 0f, 1f);
    [SerializeField, FormerlySerializedAs("lineColor"), Tooltip("Color de las líneas cuando falta algo (blanco).")] Color colorLineas = Color.white;
    [SerializeField, FormerlySerializedAs("correctColor"), Tooltip("Color de nodos y líneas cuando la pose está bien (verde).")] Color colorCorrecto = new Color(0.15f, 1f, 0.3f, 1f);

    const int PoseShown = 17;    // puntos 0..16 de Pose
    const int HandNodes = 21;
    const int FaceNodes = 478;
    const int MaxHands = 2;

    static readonly int[,] PoseLinks =
    {
        {0,1},{1,2},{2,3},{3,7},{0,4},{4,5},{5,6},{6,8},{9,10},   // cara aproximada
        {11,12},{11,13},{13,15},{12,14},{14,16}                   // hombros y brazos
    };

    static readonly int[,] HandLinks =
    {
        {0,1},{1,2},{2,3},{3,4},                // pulgar
        {0,5},{5,6},{6,7},{7,8},                // índice
        {5,9},{9,10},{10,11},{11,12},           // medio
        {9,13},{13,14},{14,15},{15,16},         // anular
        {13,17},{17,18},{18,19},{19,20},{0,17}  // meñique y palma
    };

    // Contornos de la cara (puntos de Face Landmarker)
    static readonly int[,] FaceLinks = MakeLinks(
        new[] { 61, 146, 91, 181, 84, 17, 314, 405, 321, 375, 291, 409, 270, 269, 267, 0, 37, 39, 40, 185, 61 },   // labios (afuera)
        new[] { 78, 95, 88, 178, 87, 14, 317, 402, 318, 324, 308, 415, 310, 311, 312, 13, 82, 81, 80, 191, 78 },   // labios (adentro)
        new[] { 263, 249, 390, 373, 374, 380, 381, 382, 362, 398, 384, 385, 386, 387, 388, 466, 263 },             // ojo izquierdo
        new[] { 33, 7, 163, 144, 145, 153, 154, 155, 133, 173, 157, 158, 159, 160, 161, 246, 33 },                 // ojo derecho
        new[] { 276, 283, 282, 295, 285 }, new[] { 300, 293, 334, 296, 336 },                                       // ceja izquierda
        new[] { 46, 53, 52, 65, 55 }, new[] { 70, 63, 105, 66, 107 }                                           // ceja derecha
    );

    static int[,] MakeLinks(params int[][] chains)
    {
        var list = new List<int[]>();
        foreach (var c in chains)
            for (int i = 0; i < c.Length - 1; i++)
                list.Add(new[] { c[i], c[i + 1] });

        var arr = new int[list.Count, 2];
        for (int i = 0; i < list.Count; i++) { arr[i, 0] = list[i][0]; arr[i, 1] = list[i][1]; }
        return arr;
    }

    // Un "esqueleto" = un conjunto de nodos + las líneas que los unen
    class Skeleton
    {
        public int count;
        public int[,] links;
        public float grosorLinea;
        public float[] pending;
        public Vector2[] pos;
        public bool[] ok;
        public bool[] skip;                 // puntos que NO se dibujan
        public bool has, fresh, dirty;
        public RectTransform[] nodes, lines;
        public List<Image> nodeImages = new List<Image>();
        public List<Image> lineImages = new List<Image>();
    }

    readonly object gate = new object();
    Skeleton pose, face;
    Skeleton[] hands;
    Skeleton[] all;
    RectTransform rt;
    bool showingCorrect;
    static Sprite circle;

    void Awake()
    {
        rt = (RectTransform)transform;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        // El orden de creación es el orden de dibujo (lo último queda encima)
        pose = Build("pose", PoseShown, PoseLinks, tamanoNodo, grosorLinea);
        hands = new Skeleton[MaxHands];
        for (int i = 0; i < MaxHands; i++) hands[i] = Build("hand" + i, HandNodes, HandLinks, tamanoNodo, grosorLinea);
        face = Build("face", FaceNodes, FaceLinks, tamanoNodoCara, grosorLineaCara);

        all = new Skeleton[MaxHands + 2];
        all[0] = pose;
        for (int i = 0; i < MaxHands; i++) all[i + 1] = hands[i];
        all[MaxHands + 1] = face;
    }

    Skeleton Build(string tag, int count, int[,] links, float nSize, float lWidth)
    {
        var s = new Skeleton
        {
            count = count,
            links = links,
            grosorLinea = lWidth,
            pending = new float[count * 2],
            pos = new Vector2[count],
            ok = new bool[count],
            skip = new bool[count]
        };

        // Solo se crean nodos para los puntos que realmente se usan
        var used = new bool[count];
        int lc = links.GetLength(0);
        for (int k = 0; k < lc; k++) { used[links[k, 0]] = true; used[links[k, 1]] = true; }

        // Primero las líneas (quedan debajo) y luego los nodos (quedan encima)
        s.lines = new RectTransform[lc];
        for (int i = 0; i < lc; i++)
        {
            var img = CreateImage(tag + "_line" + i, null, colorLineas, new Vector2(10f, lWidth));
            s.lines[i] = img.rectTransform;
            s.lineImages.Add(img);
        }

        var sprite = GetCircle();
        s.nodes = new RectTransform[count];
        for (int i = 0; i < count; i++)
            if (used[i])
            {
                var img = CreateImage(tag + "_node" + i, sprite, colorNodos, new Vector2(nSize, nSize));
                s.nodes[i] = img.rectTransform;
                s.nodeImages.Add(img);
            }

        return s;
    }

    Image CreateImage(string objName, Sprite sprite, Color color, Vector2 size)
    {
        var go = new GameObject(objName, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(transform, false);
        var img = go.GetComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.raycastTarget = false;
        img.rectTransform.sizeDelta = size;
        go.SetActive(false);
        return img;
    }

    void Start()
    {
        if (detectorHola == null) detectorHola = FindObjectOfType<HolaSignDetector>();

        if (PoseLandmarkerRunner.Instance != null) PoseLandmarkerRunner.Instance.OnResult += FeedPose;
        else Debug.LogWarning("PoseDotsOverlay: no hay PoseLandmarkerRunner.Instance");

        if (HandLandmarkerRunner.Instance != null) HandLandmarkerRunner.Instance.OnResult += FeedHand;
        else Debug.LogWarning("PoseDotsOverlay: no hay HandLandmarkerRunner.Instance");

        if (FaceLandmarkerRunner.Instance != null)
        {
            FaceLandmarkerRunner.Instance.OnResult += FeedFace;
            // La cara ya la dibuja Face (cejas y labios exactos): se ocultan los puntos de cara de Pose
            for (int i = 0; i <= 10; i++) pose.skip[i] = true;
        }
    }

    void OnDestroy()
    {
        if (PoseLandmarkerRunner.Instance != null) PoseLandmarkerRunner.Instance.OnResult -= FeedPose;
        if (HandLandmarkerRunner.Instance != null) HandLandmarkerRunner.Instance.OnResult -= FeedHand;
        if (FaceLandmarkerRunner.Instance != null) FaceLandmarkerRunner.Instance.OnResult -= FeedFace;
    }

    // ---------- Entrada de datos (pueden llegar en otro hilo: solo guardar números) ----------

    void FeedPose(PoseLandmarkerResult r)
    {
        bool has = r.poseLandmarks != null && r.poseLandmarks.Count > 0;
        lock (gate)
        {
            pose.has = has;
            if (has) Fill(pose, r.poseLandmarks[0].landmarks);
            pose.fresh = true;
        }
    }

    void FeedHand(HandLandmarkerResult r)
    {
        int n = r.handLandmarks != null ? r.handLandmarks.Count : 0;
        lock (gate)
        {
            for (int h = 0; h < MaxHands; h++)
            {
                hands[h].has = h < n;
                if (h < n) Fill(hands[h], r.handLandmarks[h].landmarks);
                hands[h].fresh = true;
            }
        }
    }

    void FeedFace(FaceLandmarkerResult r)
    {
        bool has = r.faceLandmarks != null && r.faceLandmarks.Count > 0;
        lock (gate)
        {
            face.has = has;
            if (has) Fill(face, r.faceLandmarks[0].landmarks);
            face.fresh = true;
        }
    }

    static void Fill(Skeleton s, IReadOnlyList<NormalizedLandmark> lm)
    {
        int n = Mathf.Min(s.count, lm.Count);
        for (int i = 0; i < n; i++)
        {
            s.pending[i * 2] = lm[i].x;
            s.pending[i * 2 + 1] = lm[i].y;
        }
    }

    // ---------- Dibujo (hilo principal) ----------

    void Update()
    {
        // Verde mientras la pose está bien hecha
        bool correct = detectorHola != null && detectorHola.PoseCorrect;
        if (correct != showingCorrect)
        {
            showingCorrect = correct;
            ApplyColors();
        }

        float w = rt.rect.width, h = rt.rect.height;

        lock (gate)
        {
            for (int i = 0; i < all.Length; i++) Compute(all[i], w, h);
        }

        for (int i = 0; i < all.Length; i++) Draw(all[i]);
    }

    void ApplyColors()
    {
        Color nc = showingCorrect ? colorCorrecto : colorNodos;
        Color lc = showingCorrect ? colorCorrecto : colorLineas;
        for (int i = 0; i < all.Length; i++)
        {
            foreach (var img in all[i].nodeImages) img.color = nc;
            foreach (var img in all[i].lineImages) img.color = lc;
        }
    }

    void Compute(Skeleton s, float w, float h)
    {
        if (!s.fresh) return;
        s.fresh = false;
        s.dirty = true;

        for (int i = 0; i < s.count; i++)
        {
            float x = s.pending[i * 2];
            float y = s.pending[i * 2 + 1];

            // Solo se dibuja si hay datos, el punto no está oculto y está dentro de la imagen
            s.ok[i] = s.has && !s.skip[i] && x >= 0f && x <= 1f && y >= 0f && y <= 1f;

            if (invertirX) x = 1f - x;
            s.pos[i] = new Vector2((x - 0.5f) * w, (0.5f - y) * h);
        }
    }

    void Draw(Skeleton s)
    {
        if (!s.dirty) return;
        s.dirty = false;

        for (int i = 0; i < s.count; i++)
        {
            if (s.nodes[i] == null) continue;
            s.nodes[i].gameObject.SetActive(s.ok[i]);
            if (s.ok[i]) s.nodes[i].anchoredPosition = s.pos[i];
        }

        for (int k = 0; k < s.lines.Length; k++)
        {
            int a = s.links[k, 0], b = s.links[k, 1];
            bool show = s.ok[a] && s.ok[b];
            s.lines[k].gameObject.SetActive(show);
            if (!show) continue;

            Vector2 d = s.pos[b] - s.pos[a];
            s.lines[k].anchoredPosition = (s.pos[a] + s.pos[b]) * 0.5f;
            s.lines[k].sizeDelta = new Vector2(d.magnitude, s.grosorLinea);
            s.lines[k].localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg);
        }
    }

    static Sprite GetCircle()
    {
        if (circle != null) return circle;
        const int s = 32;
        var tex = new Texture2D(s, s, TextureFormat.RGBA32, false);
        var px = new Color32[s * s];
        float c = (s - 1) / 2f, rad = s / 2f;
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                px[y * s + x] = d <= rad - 1f ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
            }
        tex.SetPixels32(px);
        tex.Apply();
        circle = Sprite.Create(tex, new UnityEngine.Rect(0, 0, s, s), new Vector2(0.5f, 0.5f));
        return circle;
    }
}