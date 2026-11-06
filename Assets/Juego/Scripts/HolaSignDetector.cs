using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.Events;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using Mediapipe.Tasks.Vision.HandLandmarker;
using Mediapipe.Tasks.Vision.PoseLandmarker;
using Mediapipe.Unity.Sample.FaceLandmarkDetection;
using Mediapipe.Unity.Sample.HandLandmarkDetection;
using Mediapipe.Unity.Sample.PoseLandmarkDetection;

/// <summary>Los pasos de la seña HOLA, en orden.</summary>
public enum HolaStep
{
    Calibrando,     // "Detectando estado en reposo: manos abajo, expresión neutra"
    MuestraMano,    // "Muestra tu mano (cejas relajadas)"
    AbreMano,       // "Abre la mano (5)"
    LlevaMano,      // "Lleva la mano a la sien"
    LevantaCejas,   // "Levanta las cejas"
    AlejaMano,      // "Aleja la mano hacia adelante"
    Reintentar      // "Intenta de nuevo"
}

/// <summary>
/// Seña HOLA (LSC), con un paso por cada mensaje:
///  0) Calibrando    -> manos abajo y cara neutra durante 2 s: se guarda tu rostro en reposo
///  1) MuestraMano   -> no se ve la mano (o la cabeza)
///  2) AbreMano      -> la mano se ve pero no está abierta ("5")
///  3) LlevaMano     -> la mano abierta aún no está junto a la sien (ni muy lejos ni tapando la cara)
///  4) LevantaCejas  -> mano en la sien, faltan las cejas elevadas (comparadas con tu reposo)
///  5) AlejaMano     -> la mano se aleja de la cabeza hacia el interlocutor
///  6) Reintentar    -> te tardaste más de segundosMaxAlejar o cerraste la mano
/// Mientras la pose está bien hecha, PoseCorrect = true (PoseDotsOverlay pinta todo de verde).
/// Necesita Hand y Pose runners. Con Face runner también guarda y compara cejas y boca.
/// </summary>
public class HolaSignDetector : MonoBehaviour
{
    [Header("Estado en reposo (calibración inicial)")]
    [SerializeField, FormerlySerializedAs("calibSeconds"), Tooltip("Segundos que se graba el rostro en reposo (manos abajo, expresión neutra).")] float segundosCalibracion = 2f;
    [SerializeField, FormerlySerializedAs("calibTolerance"), Tooltip("Si las cejas se mueven más de este porcentaje (0.12 = 12%) durante la grabación, se reinicia.")] float toleranciaCejas = 0.12f;
    [SerializeField, FormerlySerializedAs("mouthTolerance"), Tooltip("Si la boca se mueve más de esto durante la grabación, se reinicia.")] float toleranciaBoca = 0.02f;
    [SerializeField, FormerlySerializedAs("calibDoneShowSeconds"), Tooltip("Segundos que se ve el mensaje 'Estado en reposo guardado'.")] float segundosMensajeReposoGuardado = 1f;

    [Header("Cejas y boca (comparadas con tu reposo)")]
    [SerializeField, FormerlySerializedAs("requireBrows"), Tooltip("Exigir cejas elevadas para la seña (solo si hay Face Landmarker Runner).")] bool exigirCejas = true;
    [SerializeField, FormerlySerializedAs("browFactor"), Tooltip("1.12 = las cejas deben estar 12% más altas que en reposo. Bájalo si no se detectan.")] float factorCejas = 1.12f;
    [SerializeField, FormerlySerializedAs("requireMouth"), Tooltip("Exigir que muevas la boca (decir «hola»).")] bool exigirBoca = false;
    [SerializeField, FormerlySerializedAs("mouthDelta"), Tooltip("Cuánto más abierta que en reposo debe estar la boca (relativo al alto de la cara).")] float aperturaBocaMinima = 0.03f;

    [Header("Posición de la mano junto a la sien (en 'anchos de cabeza')")]
    [SerializeField, FormerlySerializedAs("minHeadDistance"), Tooltip("En anchos de cabeza. Más cerca que esto = la mano tapa la cara y no cuenta.")] float distanciaMinimaCabeza = 0.8f;
    [SerializeField, FormerlySerializedAs("maxHeadDistance"), Tooltip("En anchos de cabeza. Más lejos que esto = la mano no está en la sien.")] float distanciaMaximaCabeza = 1.6f;
    [SerializeField, FormerlySerializedAs("awayTravel"), Tooltip("Cuánto debe alejarse la mano (en anchos de cabeza) desde donde empezó.")] float recorridoAlejar = 0.8f;

    [Header("Tiempos (segundos)")]
    [SerializeField, FormerlySerializedAs("holdAtHead"), Tooltip("Tiempo mínimo con la mano en la sien antes de pasar a alejarla.")] float segundosManoEnSien = 0.15f;
    [SerializeField, FormerlySerializedAs("maxMoveTime"), Tooltip("Tiempo máximo para alejar la mano; si tardas más sale 'Intenta de nuevo'.")] float segundosMaxAlejar = 1.2f;
    [SerializeField, FormerlySerializedAs("retryShowSeconds"), Tooltip("Segundos que se ve 'Intenta de nuevo'.")] float segundosMensajeReintentar = 1.0f;
    [SerializeField, FormerlySerializedAs("cooldown"), Tooltip("Segundos de espera antes de poder lograr la seña otra vez.")] float segundosEsperaEntreIntentos = 1.5f;
    [SerializeField, FormerlySerializedAs("headTimeout"), Tooltip("Segundos que se tolera perder la cabeza antes de reiniciar.")] float segundosToleranciaCabeza = 0.5f;
    [SerializeField, FormerlySerializedAs("greenHold"), Tooltip("Segundos que el verde sigue después de perder la pose (más alto = tarda más en volver a naranja).")] float segundosVerdeExtra = 1.5f;
    [SerializeField, FormerlySerializedAs("greenAfterSuccess"), Tooltip("Segundos que el verde se mantiene al lograr la seña.")] float segundosVerdeAlLograr = 2.5f;

    [Header("Cámara y ayuda")]
    [SerializeField, FormerlySerializedAs("aspect"), Tooltip("Ancho/alto de la cámara: 1.78 para 1280x720, 1.33 para 640x480.")] float proporcionCamara = 1.78f;
    [SerializeField, FormerlySerializedAs("debugOnScreen"), Tooltip("Muestra en pantalla distancias y cambios para ajustar los valores.")] bool mostrarDepuracion = false;

    [FormerlySerializedAs("OnHola"), Tooltip("Se ejecuta cuando se logra la seña HOLA. Conecta aquí lo que debe pasar.")] public UnityEvent alLograrHola;
    public float Progress { get; private set; }      // 0..1 (durante la calibración = avance de la grabación)
    public string Status { get; private set; } = "Esperando mano y cabeza";
    public HolaStep Step => step;
    public bool Calibrated => calibrated;
    public bool PoseCorrect => Time.time - lastPoseOkTime < segundosVerdeExtra || Time.time < greenUntil;   // true = pose bien hecha

    /// <summary>Vuelve a grabar el estado en reposo (por ejemplo, desde un botón).</summary>
    public void Recalibrar()
    {
        calibrated = false;
        RestartCalibration();
        ClearProgress();
        Enter(HolaStep.Calibrando, "Detectando estado en reposo: manos abajo, expresión neutra");
    }

    // Datos pendientes escritos desde otros hilos
    readonly object gate = new object();
    bool handHas, handOpen;
    float handX, handY;
    bool headHas;
    float headX, headY, headSize;
    bool faceHas;
    float browRatio, mouthRatio;

    float lastHeadTime = -99f;
    bool faceAvailable;

    // Rostro en reposo (se graba al inicio)
    bool calibrated;
    float calibTimer, calibBrowSum, calibMouthSum;
    int calibSamples;
    float browBase, mouthBase, calibDoneUntil = -99f;
    float browChange, mouthChange;   // cambio actual respecto al reposo (para depurar)

    bool browSeen, mouthSeen, poseOk;
    float greenUntil = -99f, lastPoseOkTime = -99f;

    HolaStep step = HolaStep.Calibrando;
    float atHeadTimer, phaseStart, startDistance, retryUntil = -99f, lastFire = -99f;
    float handDistance;

    void Start()
    {
        if (HandLandmarkerRunner.Instance != null) HandLandmarkerRunner.Instance.OnResult += FeedHand;
        else Debug.LogWarning("HolaSignDetector: no hay HandLandmarkerRunner.Instance");

        if (PoseLandmarkerRunner.Instance != null) PoseLandmarkerRunner.Instance.OnResult += FeedPose;
        else Debug.LogWarning("HolaSignDetector: no hay PoseLandmarkerRunner.Instance");

        if (FaceLandmarkerRunner.Instance != null)
        {
            FaceLandmarkerRunner.Instance.OnResult += FeedFace;
            faceAvailable = true;
        }
        else Debug.LogWarning("HolaSignDetector: no hay FaceLandmarkerRunner.Instance (no se guardan cejas ni boca)");

        Status = "Detectando estado en reposo: manos abajo, expresión neutra";
    }

    void OnDestroy()
    {
        if (HandLandmarkerRunner.Instance != null) HandLandmarkerRunner.Instance.OnResult -= FeedHand;
        if (PoseLandmarkerRunner.Instance != null) PoseLandmarkerRunner.Instance.OnResult -= FeedPose;
        if (FaceLandmarkerRunner.Instance != null) FaceLandmarkerRunner.Instance.OnResult -= FeedFace;
    }

    // ================= Entrada de datos (pueden llegar en otro hilo: solo guardar) =================

    void FeedHand(HandLandmarkerResult r)
    {
        bool has = r.handLandmarks != null && r.handLandmarks.Count > 0;
        float x = 0f, y = 0f; bool open = false;
        if (has)
        {
            var lm = r.handLandmarks[0].landmarks;
            x = lm[9].x; y = lm[9].y;            // base del dedo medio ≈ centro de la palma
            open = IsOpen(lm);
        }
        lock (gate) { handHas = has; handX = x; handY = y; handOpen = open; }
    }

    void FeedPose(PoseLandmarkerResult r)
    {
        bool has = r.poseLandmarks != null && r.poseLandmarks.Count > 0;
        float x = 0f, y = 0f, size = 0f;
        if (has)
        {
            var lm = r.poseLandmarks[0].landmarks;
            x = lm[0].x; y = lm[0].y;            // nariz = centro de la cabeza
            size = Dist(lm[7].x, lm[7].y, lm[8].x, lm[8].y); // distancia entre orejas = "ancho de cabeza"
            if (size < 0.01f) has = false;
        }
        lock (gate)
        {
            headHas = has;
            if (has) { headX = x; headY = y; headSize = size; }
        }
    }

    // Mide dos cosas de la cara, ambas relativas al alto de la cara (así no dependen de la distancia a la cámara):
    //  - Ceja: separación vertical entre la ceja y la esquina interna del ojo (no cambia al parpadear)
    //  - Boca: apertura entre el labio de arriba y el de abajo
    void FeedFace(FaceLandmarkerResult r)
    {
        bool has = r.faceLandmarks != null && r.faceLandmarks.Count > 0;
        float brow = 0f, mouth = 0f;
        if (has)
        {
            var lm = r.faceLandmarks[0].landmarks;
            if (lm.Count > 362)
            {
                float faceH = Dist(lm[10].x, lm[10].y, lm[152].x, lm[152].y);   // frente a barbilla
                if (faceH > 0.01f)
                {
                    float left = Mathf.Abs(lm[362].y - lm[334].y);    // ceja izq - esquina interna del ojo
                    float right = Mathf.Abs(lm[133].y - lm[105].y);   // ceja der - esquina interna del ojo
                    brow = (left + right) * 0.5f / faceH;
                    mouth = Dist(lm[13].x, lm[13].y, lm[14].x, lm[14].y) / faceH;
                }
                else has = false;
            }
            else has = false;
        }
        lock (gate) { faceHas = has; browRatio = brow; mouthRatio = mouth; }
    }

    // ================= Lógica: cada fotograma se elige UN paso =================

    void Update()
    {
        // 1) Leer lo último que llegó
        bool hHas, hOpen, cHas, fHas; float hx, hy, cx, cy, cs, brow, mouth;
        lock (gate)
        {
            hHas = handHas; hOpen = handOpen; hx = handX; hy = handY;
            cHas = headHas; cx = headX; cy = headY; cs = headSize;
            fHas = faceHas; brow = browRatio; mouth = mouthRatio;
        }

        float now = Time.time;
        if (cHas) lastHeadTime = now;
        bool headOk = now - lastHeadTime < segundosToleranciaCabeza && cs > 0f;
        bool visible = hHas && headOk;

        handDistance = visible ? Dist(hx, hy, cx, cy) / cs : 0f;   // distancia mano-cabeza en anchos de cabeza

        // 2) Comparar el rostro actual con el rostro en reposo
        bool browsRaised = true, mouthOpen = true;
        if (calibrated && faceAvailable)
        {
            browChange = fHas && browBase > 0f ? brow / browBase - 1f : 0f;
            mouthChange = fHas ? mouth - mouthBase : 0f;
            if (exigirCejas) browsRaised = fHas && brow > browBase * factorCejas;
            if (exigirBoca) mouthOpen = fHas && mouth - mouthBase > aperturaBocaMinima;
        }

        // 3) Elegir y ejecutar el paso que corresponde
        if (!calibrated) StepCalibrando(hHas, headOk, fHas, brow, mouth, now);
        else if (now < calibDoneUntil) poseOk = false;   // se ve "Estado en reposo guardado"
        else if (now < retryUntil) StepReintentar();
        else if (!visible) StepMuestraMano(hHas);
        else if (step == HolaStep.AlejaMano) StepAlejaMano(hOpen, handDistance, now);
        else if (!hOpen) StepAbreMano();
        else if (handDistance < distanciaMinimaCabeza || handDistance > distanciaMaximaCabeza) StepLlevaMano(handDistance);
        else StepLevantaCejas(browsRaised, mouthOpen, handDistance, now);

        if (poseOk) lastPoseOkTime = now;   // recuerda la última vez que la pose estuvo bien
    }

    // ---------- Paso 0: "Detectando estado en reposo: manos abajo, expresión neutra" ----------
    void StepCalibrando(bool hHas, bool headOk, bool fHas, float brow, float mouth, float now)
    {
        // Condiciones: sin manos a la vista, cabeza (y cara) visibles, expresión estable
        if (hHas)
        {
            RestartCalibration();
            Enter(HolaStep.Calibrando, "Detectando estado en reposo: baja las manos");
            return;
        }

        if (!headOk || (faceAvailable && !fHas))
        {
            RestartCalibration();
            Enter(HolaStep.Calibrando, "Detectando estado en reposo: mira a la cámara");
            return;
        }

        if (faceAvailable && calibSamples > 15)
        {
            float meanBrow = calibBrowSum / calibSamples;
            float meanMouth = calibMouthSum / calibSamples;
            if (Mathf.Abs(brow - meanBrow) > meanBrow * toleranciaCejas || Mathf.Abs(mouth - meanMouth) > toleranciaBoca)
            {
                RestartCalibration();
                Enter(HolaStep.Calibrando, "Detectando estado en reposo: mantén la expresión neutra");
                return;
            }
        }

        // Todo en orden: se sigue grabando
        calibTimer += Time.deltaTime;
        if (faceAvailable) { calibBrowSum += brow; calibMouthSum += mouth; calibSamples++; }
        Progress = Mathf.Clamp01(calibTimer / segundosCalibracion);
        Enter(HolaStep.Calibrando, "Detectando estado en reposo: manos abajo, expresión neutra");

        if (calibTimer >= segundosCalibracion)
        {
            if (faceAvailable && calibSamples > 0)
            {
                browBase = calibBrowSum / calibSamples;     // rostro en reposo (promedio de 2 s)
                mouthBase = calibMouthSum / calibSamples;
            }
            calibrated = true;
            calibDoneUntil = now + segundosMensajeReposoGuardado;
            Progress = 0f;
            Enter(HolaStep.MuestraMano, "Estado en reposo guardado");
        }
    }

    // ---------- Paso 1: "Muestra tu mano (cejas relajadas)" ----------
    void StepMuestraMano(bool handVisible)
    {
        Enter(HolaStep.MuestraMano, handVisible ? "Buscando tu cabeza" : "Muestra tu mano (cejas relajadas)");
        ClearProgress();
    }

    // ---------- Paso 2: "Abre la mano (5)" ----------
    void StepAbreMano()
    {
        Enter(HolaStep.AbreMano, "Abre la mano (5)");
        ClearProgress();
    }

    // ---------- Paso 3: "Lleva la mano a la sien" ----------
    void StepLlevaMano(float d)
    {
        Enter(HolaStep.LlevaMano, d < distanciaMinimaCabeza ? "Separa la mano de la cara (junto a la sien)" : "Lleva la mano a la sien");
        ClearProgress();
    }

    // ---------- Paso 4: "Levanta las cejas" ----------
    void StepLevantaCejas(bool browsRaised, bool mouthOpen, float d, float now)
    {
        if (browsRaised) browSeen = true;
        if (mouthOpen) mouthSeen = true;
        atHeadTimer += Time.deltaTime;

        if (!browSeen || !mouthSeen)
        {
            Enter(HolaStep.LevantaCejas, !browSeen ? "Levanta las cejas" : "Di «hola» (mueve la boca)");
            poseOk = false;
            Progress = 0f;
            return;
        }

        poseOk = true;                                   // mano en la sien + cejas arriba = verde
        Enter(HolaStep.LevantaCejas, "Mano en la cabeza");

        if (atHeadTimer >= segundosManoEnSien)                   // pasa al siguiente paso
        {
            startDistance = d;
            phaseStart = now;
            Enter(HolaStep.AlejaMano, "Aleja la mano hacia adelante");
        }
    }

    // ---------- Paso 5: "Aleja la mano hacia adelante (45%)" ----------
    void StepAlejaMano(bool hOpen, float d, float now)
    {
        if (!hOpen || now - phaseStart > segundosMaxAlejar)    // cerró la mano o tardó demasiado
        {
            Fail(now);
            return;
        }

        poseOk = true;
        Progress = Mathf.Clamp01((d - startDistance) / recorridoAlejar);
        Enter(HolaStep.AlejaMano, "Aleja la mano hacia adelante");

        if (Progress >= 1f && now - lastFire > segundosEsperaEntreIntentos)
            Succeed(now);
    }

    // ---------- Paso 6: "Intenta de nuevo" ----------
    void StepReintentar()
    {
        Enter(HolaStep.Reintentar, "Intenta de nuevo");
        poseOk = false;
    }

    // ---------- Resultados ----------
    void Fail(float now)
    {
        retryUntil = now + segundosMensajeReintentar;
        ClearProgress();
        Enter(HolaStep.Reintentar, "Intenta de nuevo");
    }

    void Succeed(float now)
    {
        lastFire = now;
        greenUntil = now + segundosVerdeAlLograr;            // verde un rato más al lograrlo
        ClearProgress();
        Enter(HolaStep.MuestraMano, "¡HOLA detectado!");
        alLograrHola?.Invoke();
    }

    // ---------- Utilidades de pasos ----------
    void Enter(HolaStep s, string status)
    {
        step = s;
        Status = status;
    }

    void ClearProgress()
    {
        atHeadTimer = 0f;
        Progress = 0f;
        poseOk = false;
        browSeen = false;
        mouthSeen = false;
    }

    void RestartCalibration()
    {
        calibTimer = 0f;
        calibBrowSum = 0f;
        calibMouthSum = 0f;
        calibSamples = 0;
        Progress = 0f;
    }

    // ================= Utilidades =================

    float Dist(float ax, float ay, float bx, float by)
    {
        float dx = (ax - bx) * proporcionCamara, dy = ay - by;   // corrige la proporción de la imagen
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    // Dedo extendido = la punta está más lejos de la muñeca que su articulación PIP
    bool IsOpen(IReadOnlyList<NormalizedLandmark> lm)
    {
        int[] tips = { 8, 12, 16, 20 };
        int[] pips = { 6, 10, 14, 18 };
        for (int i = 0; i < 4; i++)
        {
            float dTip = Dist(lm[tips[i]].x, lm[tips[i]].y, lm[0].x, lm[0].y);
            float dPip = Dist(lm[pips[i]].x, lm[pips[i]].y, lm[0].x, lm[0].y);
            if (dTip < dPip * 1.1f) return false;
        }
        return true;
    }

    void OnGUI()
    {
        if (!mostrarDepuracion) return;
        GUI.Label(new UnityEngine.Rect(10, 10, 900, 30),
            $"Paso: {step}   |   Distancia mano-cabeza: {handDistance:0.00} (válido {distanciaMinimaCabeza:0.00}-{distanciaMaximaCabeza:0.00})");
        GUI.Label(new UnityEngine.Rect(10, 34, 900, 30),
            $"Reposo guardado: {calibrated}   |   Cambio de cejas: {browChange * 100f:+0;-0;0}% (se necesita +{(factorCejas - 1f) * 100f:0}%)   |   Cambio de boca: {mouthChange:+0.000;-0.000;0.000}");
    }
}