using System.Collections.Generic;
using UnityEngine;

namespace JunkRace
{
    /// <summary>
    /// Зонт: в кузове на спец-кнопке раскрывается и тормозит падение.
    /// Где бы ни стоял, ловит воздух (airDrag) — на багажнике просто снижает скорость.
    /// </summary>
    public class UmbrellaPart : Part
    {
        [SerializeField] float drag = 150f;
        [SerializeField] Transform canopy;
        bool open;

        public override bool WorksInBody => true;

        public override void DescribeStats(List<string> lines)
        {
            lines.Add("Пробел: тормозит падение");
            base.DescribeStats(lines);
        }

        public override void Tick(float dt)
        {
            open = Input.Special;
            if (!open) return;
            var body = Vehicle.Body;
            float vy = body.linearVelocity.y;
            if (vy < 0f) body.AddForceAtPosition(Vector3.up * -vy * drag, transform.position);
        }

        public override void OnDetached()
        {
            base.OnDetached();
            open = false;
        }

        void Update()
        {
            if (!canopy) return;
            float target = open ? 1f : 0.25f;
            Vector3 s = canopy.localScale;
            float x = Mathf.MoveTowards(s.x, target * 2f, Time.deltaTime * 8f);
            canopy.localScale = new Vector3(x, s.y, x);
        }
    }
}
