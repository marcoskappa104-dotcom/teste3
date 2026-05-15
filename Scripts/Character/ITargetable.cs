using UnityEngine;

namespace RPG.Character
{
    /// <summary>
    /// Entidade selecionável por raycast: monstros, NPCs, jogadores.
    /// Toda lógica de dano é server-authoritative — não há TakeDamage no cliente.
    /// </summary>
    public interface ITargetable
    {
        string  DisplayName { get; }
        float   CurrentHP   { get; }
        float   MaxHP       { get; }
        bool    IsDead      { get; }
        Vector3 Position    { get; }

        void OnSelected();
        void OnDeselected();
    }

    /// <summary>
    /// Componente base para entidades selecionáveis.
    /// Gerencia apenas o indicador visual de seleção.
    /// </summary>
    public abstract class TargetableEntity : MonoBehaviour, ITargetable
    {
        [Header("Targetable")]
        [SerializeField] protected string     displayName        = "Entity";
        [SerializeField] protected GameObject selectionIndicator;

        public virtual string  DisplayName => displayName;
        public virtual Vector3 Position    => transform.position;

        public abstract float CurrentHP { get; }
        public abstract float MaxHP     { get; }
        public abstract bool  IsDead    { get; }

        public virtual void OnSelected()
        {
            if (selectionIndicator != null)
                selectionIndicator.SetActive(true);
        }

        public virtual void OnDeselected()
        {
            if (selectionIndicator != null)
                selectionIndicator.SetActive(false);
        }

        protected virtual void Awake()
        {
            if (selectionIndicator != null)
                selectionIndicator.SetActive(false);
        }
    }
}
