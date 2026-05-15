using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using TMPro;

namespace RPG.UI
{
    /// <summary>
    /// Pool de textos flutuantes (dano, cura, XP, etc).
    /// Desabilita-se automaticamente em servidor dedicado (Application.isBatchMode).
    ///
    /// === MUDANÇAS DESTA VERSÃO (Lote 6) ===
    ///
    ///   1. CACHE DE TextMeshPro NO POOL:
    ///      Antes, cada Show fazia GetComponent<TextMeshPro> no objeto puxado
    ///      do pool. Agora cacheamos o componente no momento do prewarm,
    ///      usando uma struct PoolEntry. Em combates com muito dano flutuante,
    ///      isso reduz dezenas de GetComponent por segundo.
    ///
    ///   2. POOL CRESCE SUAVEMENTE:
    ///      Antes, se o pool esvaziasse, Instantiate criava UM por vez sem
    ///      cachear o resultado. Agora o pool cresce respeitando o padrão
    ///      de PoolEntry, mantendo cache consistente.
    /// </summary>
    public class FloatingTextManager : MonoBehaviour
    {
        public static FloatingTextManager Instance { get; private set; }

        [SerializeField] private GameObject floatingTextPrefab;
        [SerializeField] private int        poolSize  = 20;
        [SerializeField] private float      riseSpeed = 2f;
        [SerializeField] private float      lifetime  = 1.2f;

        /// <summary>
        /// Entrada do pool com componentes pré-cacheados.
        /// </summary>
        private struct PoolEntry
        {
            public GameObject  Obj;
            public TextMeshPro Tmp;
        }

        private readonly Queue<PoolEntry> _pool = new Queue<PoolEntry>();
        private Camera                    _cachedCamera;
        private bool                      _isServerOnly;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            if (Application.isBatchMode)
            {
                _isServerOnly = true;
                Debug.Log("[FloatingTextManager] Servidor dedicado — UI desabilitada.");
                return;
            }

            PrewarmPool();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            if (!_isServerOnly)
                SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void Start()
        {
            if (_isServerOnly) return;
            _cachedCamera = Camera.main;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // A câmera pode mudar entre cenas
            _cachedCamera = Camera.main;
        }

        private PoolEntry CreateEntry()
        {
            var obj = Instantiate(floatingTextPrefab, transform);
            obj.SetActive(false);
            var tmp = obj.GetComponent<TextMeshPro>()
                   ?? obj.GetComponentInChildren<TextMeshPro>();
            return new PoolEntry { Obj = obj, Tmp = tmp };
        }

        private void PrewarmPool()
        {
            if (floatingTextPrefab == null)
            {
                Debug.LogWarning("[FloatingTextManager] floatingTextPrefab não configurado.");
                return;
            }

            int size = Mathf.Max(poolSize, 1);
            for (int i = 0; i < size; i++)
                _pool.Enqueue(CreateEntry());
        }

        public void Show(string text, Vector3 worldPos, Color color)
        {
            if (_isServerOnly || Application.isBatchMode) return;
            if (floatingTextPrefab == null) return;

            if (_cachedCamera == null) _cachedCamera = Camera.main;

            StartCoroutine(ShowCoroutine(text, worldPos, color, _cachedCamera));
        }

        private IEnumerator ShowCoroutine(string text, Vector3 worldPos, Color color, Camera cam)
        {
            PoolEntry entry = _pool.Count > 0
                ? _pool.Dequeue()
                : CreateEntry();

            var obj = entry.Obj;
            var tmp = entry.Tmp;

            obj.transform.position = worldPos + new Vector3(Random.Range(-0.3f, 0.3f), 0f, 0f);
            obj.SetActive(true);

            if (tmp != null)
            {
                tmp.text  = text;
                tmp.color = color;
            }

            float   elapsed  = 0f;
            Vector3 startPos = obj.transform.position;

            while (elapsed < lifetime)
            {
                elapsed += Time.deltaTime;
                float t = elapsed / lifetime;

                obj.transform.position = startPos + Vector3.up * (riseSpeed * t);

                if (tmp != null)
                {
                    var c = tmp.color;
                    c.a       = 1f - Mathf.Pow(t, 2f);
                    tmp.color = c;
                }

                if (cam != null)
                {
                    Vector3 dir = obj.transform.position - cam.transform.position;
                    if (dir.sqrMagnitude > 0.001f)
                        obj.transform.forward = dir.normalized;
                }

                yield return null;
            }

            obj.SetActive(false);
            _pool.Enqueue(entry);
        }
    }
}