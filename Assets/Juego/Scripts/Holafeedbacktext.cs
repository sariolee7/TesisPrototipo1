using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Muestra en un texto qué le falta a la seña HOLA, y "¡Lo conseguiste!" al lograrla.
/// Ponlo en el mismo objeto que tiene el Text (TextMeshPro o Text normal).
/// </summary>
public class HolaFeedbackText : MonoBehaviour
{
    [SerializeField, FormerlySerializedAs("hola"), Tooltip("Detector de la seña. Si está vacío se busca solo en la escena.")] HolaSignDetector detectorHola;
    [SerializeField, FormerlySerializedAs("tmpText"), Tooltip("Texto TextMeshPro donde se muestra el mensaje. Si está vacío se busca en este objeto.")] TMP_Text textoTMP;
    [SerializeField, FormerlySerializedAs("uiText"), Tooltip("Texto normal de Unity (si no usas TextMeshPro).")] Text textoUI;

    [Header("Mensajes")]
    [SerializeField, FormerlySerializedAs("successMessage"), Tooltip("Mensaje al lograr la seña.")] string mensajeExito = "¡Lo conseguiste!";
    [SerializeField, FormerlySerializedAs("successSeconds"), Tooltip("Segundos que se queda el mensaje de éxito.")] float segundosMensajeExito = 2.5f;
    [SerializeField, FormerlySerializedAs("showPercent"), Tooltip("Muestra el avance en porcentaje al alejar la mano.")] bool mostrarPorcentaje = true;

    [Header("Colores")]
    [SerializeField, FormerlySerializedAs("missingColor"), Tooltip("Color del texto mientras falta algo (naranja).")] Color colorFalta = new Color(1f, 0.6f, 0.1f, 1f);
    [SerializeField, FormerlySerializedAs("successColor"), Tooltip("Color del texto cuando la pose está bien (verde).")] Color colorExito = new Color(0.15f, 0.85f, 0.3f, 1f);

    float successUntil = -99f;

    void Start()
    {
        if (detectorHola == null) detectorHola = FindObjectOfType<HolaSignDetector>();
        if (textoTMP == null) textoTMP = GetComponent<TMP_Text>();
        if (textoUI == null) textoUI = GetComponent<Text>();

        if (detectorHola != null) detectorHola.alLograrHola.AddListener(OnSuccess);
        else Debug.LogWarning("HolaFeedbackText: no hay HolaSignDetector en la escena");

        if (textoTMP == null && textoUI == null)
            Debug.LogWarning("HolaFeedbackText: no encontré ningún Text en este objeto");
    }

    void OnDestroy()
    {
        if (detectorHola != null) detectorHola.alLograrHola.RemoveListener(OnSuccess);
    }

    void OnSuccess()
    {
        successUntil = Time.time + segundosMensajeExito;
    }

    void Update()
    {
        if (detectorHola == null) return;

        string msg;
        Color color;

        if (Time.time < successUntil)
        {
            msg = mensajeExito;
            color = colorExito;
        }
        else
        {
            msg = detectorHola.Status;
            if (mostrarPorcentaje && detectorHola.Progress > 0f)
                msg += $" ({Mathf.RoundToInt(detectorHola.Progress * 100f)}%)";
            color = detectorHola.PoseCorrect ? colorExito : colorFalta;
        }

        if (textoTMP != null) { textoTMP.text = msg; textoTMP.color = color; }
        if (textoUI != null) { textoUI.text = msg; textoUI.color = color; }
    }
}