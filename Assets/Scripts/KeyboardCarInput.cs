using UnityEngine;

// ==================================================================
// KEYBOARD CAR INPUT
// ------------------------------------------------------------------
// Drop this on the SAME GameObject as ArcadeCarController for a
// locally keyboard-controlled car (e.g. the player, in single-player
// or on whichever machine owns that car in multiplayer).
//
// Cars that should NOT respond to this machine's keyboard - remote
// players, cop cars, AI traffic - simply don't get this component.
// They (or your network layer / AI brain) write to
// ArcadeCarController.SteerInput / ThrottleInput directly instead.
//
// A touch-screen control scheme works the same way: a UI script with
// on-screen buttons/joystick calls car.SteerInput = x; car.ThrottleInput = y;
// each frame instead of reading Input.GetAxis. You can even have both
// components on the same car and only enable the one that matches the
// active platform.
// ==================================================================

[RequireComponent(typeof(ArcadeCarController))]
public class KeyboardCarInput : MonoBehaviour
{
    [Tooltip("Set false to temporarily hand control to another input source (touch, network, AI) without removing this component.")]
    public bool inputEnabled = true;

    ArcadeCarController car;

    void Awake()
    {
        car = GetComponent<ArcadeCarController>();
    }

    void Update()
    {
        if (!inputEnabled) return;

        car.SteerInput = Input.GetAxis("Horizontal");
        car.ThrottleInput = Input.GetAxis("Vertical");
    }
}