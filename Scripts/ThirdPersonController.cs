﻿using UnityEngine;
#if ENABLE_INPUT_SYSTEM 
using UnityEngine.InputSystem;

#endif

using tumvt.sumounity;
using static tumvt.sumounity.Vehicle;  

/* Note: animations are called via the controller for both the character and capsule using animator null checks
 */

namespace StarterAssets
{
    [RequireComponent(typeof(CharacterController))]
#if ENABLE_INPUT_SYSTEM 
    [RequireComponent(typeof(PlayerInput))]
#endif
    public class ThirdPersonController : MonoBehaviour, IVehicleController {

        public string id { get; set; } // SUMO Identifiert in Vehicle Dictionary

        [Header("SUMO Integration")]
        private SumoSocketClient sock;  // Reference to SUMO socket client
        private PIDController pidControllerSpeed;
        private PIDController pidControllerDist;
        private bool bDrawGizmo;
        private Vector2 lookAheadMarker;
        public bool isSumoVehicle = true;
        private Vector2 rbMarker;
        private float stopState;

        [Header("Player")]
        [Tooltip("Move speed of the character in m/s")]
        public float MoveSpeed = 2.0f;

        [Tooltip("Sprint speed of the character in m/s")]
        public float SprintSpeed = 5.335f;

        [Tooltip("How fast the character turns to face movement direction")]
        [Range(0.0f, 100.0f)]
        public float RotationSmoothTime = 1.0f;

        [Tooltip("Acceleration and deceleration")]
        public float SpeedChangeRate = 10.0f;

        public AudioClip LandingAudioClip;
        public AudioClip[] FootstepAudioClips;
        [Range(0, 1)] public float FootstepAudioVolume = 0.5f;

        [Space(10)]
        [Tooltip("The height the player can jump")]
        public float JumpHeight = 1.2f;

        [Tooltip("The character uses its own gravity value. The engine default is -9.81f")]
        public float Gravity = -15.0f;

        [Space(10)]
        [Tooltip("Time required to pass before being able to jump again. Set to 0f to instantly jump again")]
        public float JumpTimeout = 0.50f;

        [Tooltip("Time required to pass before entering the fall state. Useful for walking down stairs")]
        public float FallTimeout = 0.15f;

        [Header("Player Grounded")]
        [Tooltip("If the character is grounded or not. Not part of the CharacterController built in grounded check")]
        public bool Grounded = true;

        [Tooltip("Useful for rough ground")]
        public float GroundedOffset = -0.14f;

        [Tooltip("The radius of the grounded check. Should match the radius of the CharacterController")]
        public float GroundedRadius = 0.28f;

        [Tooltip("What layers the character uses as ground")]
        public LayerMask GroundLayers;

        [Header("Cinemachine")]
        [Tooltip("The follow target set in the Cinemachine Virtual Camera that the camera will follow")]
        public GameObject CinemachineCameraTarget;

        [Tooltip("How far in degrees can you move the camera up")]
        public float TopClamp = 70.0f;

        [Tooltip("How far in degrees can you move the camera down")]
        public float BottomClamp = -30.0f;

        [Tooltip("Additional degress to override the camera. Useful for fine tuning camera position when locked")]
        public float CameraAngleOverride = 0.0f;

        [Tooltip("For locking the camera position on all axis")]
        public bool LockCameraPosition = false;

        // cinemachine
        private float _cinemachineTargetYaw;
        private float _cinemachineTargetPitch;

        // player
        private float _speed;
        private float _animationBlend;
        private float _rotationVelocity;
        private float _verticalVelocity;
        private float _terminalVelocity = 53.0f;

        // timeout deltatime
        private float _jumpTimeoutDelta;
        private float _fallTimeoutDelta;

        // animation IDs
        private int _animIDSpeed;
        private int _animIDGrounded;
        private int _animIDJump;
        private int _animIDFreeFall;
        private int _animIDMotionSpeed;

#if ENABLE_INPUT_SYSTEM 
        private PlayerInput _playerInput;
#endif

        [Header("VR Settings")]
        [SerializeField]
        [Tooltip("VR Input Actions Asset")]
        // VR Input
        private InputActionAsset inputActions; // Drag your Input Actions asset here in the Inspector
        private InputActionMap xrInputActionMap;
        private InputAction thumbstickAction;
        private Vector2 thumbstickPosition;
        public float rotationSpeedGain;
        public float walkSpeedGain;

        private Animator _animator;
        private CharacterController _controller;
        private GameObject _mainCamera;

        private const float _threshold = 0.01f;

        private bool _hasAnimator;
        private Vector3 _targetDirection;
        

        private bool IsCurrentDeviceMouse
        {
            get
            {
#if ENABLE_INPUT_SYSTEM
                return _playerInput.currentControlScheme == "KeyboardMouse";
#else
				return false;
#endif
            }
        }

        Rigidbody rb;

        private bool isCurrentlyInsideVehicle = false;
        private float teleportTimer = 0f;
        private const float TELEPORT_DELAY = 4f;

        private void Start()
        {
            _cinemachineTargetYaw = CinemachineCameraTarget.transform.rotation.eulerAngles.y;
            
            _hasAnimator = TryGetComponent(out _animator);
            _controller = GetComponent<CharacterController>();
#if ENABLE_INPUT_SYSTEM 
            _playerInput = GetComponent<PlayerInput>();
#else
			Debug.LogError( "Starter Assets package is missing dependencies. Please use Tools/Starter Assets/Reinstall Dependencies to fix it");
#endif

            AssignAnimationIDs();

            // reset our timeouts on start
            _jumpTimeoutDelta = JumpTimeout;
            _fallTimeoutDelta = FallTimeout;


            // Sumo: Get the socketclient with the step info
            rb = GetComponent<Rigidbody>();

            InitializeSumoIntegration();

        }


        void OnDrawGizmos(){
            if (bDrawGizmo){
                Gizmos.color = Color.red;
                Vector3 LadPoint = new Vector3(lookAheadMarker.x, 0.1f, lookAheadMarker.y);
                Gizmos.DrawSphere(LadPoint, 1.0f);

                Gizmos.color = Color.blue;
                Vector3 rbMarkerPoint = new Vector3(rbMarker.x, 0.1f, rbMarker.y);
                Gizmos.DrawSphere(rbMarkerPoint, 1.0f);
            }
        }

         private void InitializeSumoIntegration()
        {
            // Get the socketclient with the step info
            sock = GameObject.FindObjectOfType<SumoSocketClient>();

            // Initialize controllers
            pidControllerDist = new PIDController(15.0f, 0.0f, 0.0f); 
            pidControllerSpeed = new PIDController(1.0f, 0.0f, 0.0f); 
            bDrawGizmo = true;
        }

        private void Update()
        {
            _hasAnimator = TryGetComponent(out _animator);

            JumpAndGravity();
            GroundedCheck();

            if (isSumoVehicle){
                bool isInsideVehicle = PedestrianIsInsideVehicle(ref sock, id);
                
                if (isInsideVehicle)
                {
                    teleportTimer += Time.deltaTime;
                    if (teleportTimer >= TELEPORT_DELAY)
                    {
                        TeleportSumo();
                    } else {
                        MoveSumo();
                    }
                }
                else
                {
                    teleportTimer = 0f;  // Reset timer when not inside vehicle
                    MoveSumo();
                }
                
                isCurrentlyInsideVehicle = isInsideVehicle;
            } else {
                Debug.LogWarning("Set to Sumo vehicle, manual is not implemented");
            }
        }


        private void AssignAnimationIDs()
        {
            _animIDSpeed = Animator.StringToHash("Speed");
            _animIDGrounded = Animator.StringToHash("Grounded");
            _animIDJump = Animator.StringToHash("Jump");
            _animIDFreeFall = Animator.StringToHash("FreeFall");
            _animIDMotionSpeed = Animator.StringToHash("MotionSpeed");
        }

        private void GroundedCheck()
        {
            // set sphere position, with offset
            Vector3 spherePosition = new Vector3(transform.position.x, transform.position.y - GroundedOffset,
                transform.position.z);
            Grounded = Physics.CheckSphere(spherePosition, GroundedRadius, GroundLayers,
                QueryTriggerInteraction.Ignore);

            // update animator if using character
            if (_hasAnimator)
            {
                _animator.SetBool(_animIDGrounded, Grounded);
            }
        }

        
        private void TeleportSumo(){
            Debug.LogWarning("Teleporting Sumo");   
            Vector2 pos = PedestrianGetPosition(ref sock, id);
            transform.position = new Vector3(pos.x, 0.0f, pos.y);
            rbMarker.x = pos.x;
            rbMarker.y = pos.y; 
            lookAheadMarker = rbMarker;
        }   

        private void MoveSumo(){
            // rb.isKinematic = true;
            rbMarker.x = rb.position.x;
            rbMarker.y = rb.position.z;

            var (worldMovementVector,worldMovementSpeed,worldMovementDirection,absolutePositionError,lookAheadPoint) = 
                SumoPedestrianControl(
                    ref sock, 
                    id, 
                    rb,
                    ref lookAheadMarker
                );

           // set target speed based on move speed, sprint speed and if sprint is pressed
            
            // increase speed if error is large:
            float targetSpeed = worldMovementSpeed;
            if (absolutePositionError > 0.1f){
                targetSpeed = SprintSpeed;
            }
            // float targetSpeed = worldMovementSpeed;

            // _input.move = worldMovementVector;

            if (worldMovementVector == Vector2.zero) targetSpeed = 0;    


            // a reference to the players current horizontal velocity
            float currentHorizontalSpeed = new Vector3(_controller.velocity.x, 0.0f, _controller.velocity.z).magnitude;

            float speedOffset = 0.1f;
            float inputMagnitude = 1.0f;

            // accelerate or decelerate to target speed
            if (currentHorizontalSpeed < targetSpeed - speedOffset ||
                currentHorizontalSpeed > targetSpeed + speedOffset)
            {
                // creates curved result rather than a linear one giving a more organic speed change
                // note T in Lerp is clamped, so we don't need to clamp our speed
                _speed = Mathf.Lerp(currentHorizontalSpeed, targetSpeed * inputMagnitude,
                    Time.deltaTime * SpeedChangeRate);

                // round speed to 3 decimal places
                _speed = Mathf.Round(_speed * 1000f) / 1000f;

                Debug.Log($"_speed: {_speed}");
            }
            else
            {
                _speed = targetSpeed;
            }

            _animationBlend = Mathf.Lerp(_animationBlend, targetSpeed, Time.deltaTime * SpeedChangeRate);
            if (_animationBlend < 0.01f) _animationBlend = 0f;



            // normalise input direction
            Vector3 inputDirection = new Vector3(worldMovementVector.x, 0.0f, worldMovementVector.y).normalized;

            float rotation = Mathf.SmoothDampAngle(transform.eulerAngles.y, worldMovementDirection, ref _rotationVelocity,
                RotationSmoothTime);

            if (worldMovementVector != Vector2.zero)
            {
                // rotate to face input direction relative to camera position
                transform.rotation = Quaternion.Euler(0.0f, rotation, 0.0f);
            }

            _controller.Move(inputDirection.normalized * (_speed * Time.deltaTime) +
                            new Vector3(0.0f, _verticalVelocity, 0.0f) * Time.deltaTime);

            // update animator if using character
            if (_hasAnimator)
            {
                _animator.SetFloat(_animIDSpeed, _animationBlend);
                _animator.SetFloat(_animIDMotionSpeed, inputMagnitude);
            }
        }

    

        private void JumpAndGravity()
        {
            if (Grounded)
            {
                // reset the fall timeout timer
                _fallTimeoutDelta = FallTimeout;

                // update animator if using character
                if (_hasAnimator)
                {
                    _animator.SetBool(_animIDJump, false);
                    _animator.SetBool(_animIDFreeFall, false);
                }

                // stop our velocity dropping infinitely when grounded
                if (_verticalVelocity < 0.0f)
                {
                    _verticalVelocity = -2f;
                }

                
                // jump timeout
                if (_jumpTimeoutDelta >= 0.0f)
                {
                    _jumpTimeoutDelta -= Time.deltaTime;
                }
            }
            else
            {
                // reset the jump timeout timer
                _jumpTimeoutDelta = JumpTimeout;

                // fall timeout
                if (_fallTimeoutDelta >= 0.0f)
                {
                    _fallTimeoutDelta -= Time.deltaTime;
                }
                else
                {
                    // update animator if using character
                    if (_hasAnimator)
                    {
                        _animator.SetBool(_animIDFreeFall, true);
                    }
                }

            }

            // apply gravity over time if under terminal (multiply by delta time twice to linearly speed up over time)
            if (_verticalVelocity < _terminalVelocity)
            {
                _verticalVelocity += Gravity * Time.deltaTime;
            }
        }

        private static float ClampAngle(float lfAngle, float lfMin, float lfMax)
        {
            if (lfAngle < -360f) lfAngle += 360f;
            if (lfAngle > 360f) lfAngle -= 360f;
            return Mathf.Clamp(lfAngle, lfMin, lfMax);
        }

        private void OnDrawGizmosSelected()
        {
            Color transparentGreen = new Color(0.0f, 1.0f, 0.0f, 0.35f);
            Color transparentRed = new Color(1.0f, 0.0f, 0.0f, 0.35f);

            if (Grounded) Gizmos.color = transparentGreen;
            else Gizmos.color = transparentRed;

            // when selected, draw a gizmo in the position of, and matching radius of, the grounded collider
            Gizmos.DrawSphere(
                new Vector3(transform.position.x, transform.position.y - GroundedOffset, transform.position.z),
                GroundedRadius);
        }

        private void OnFootstep(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                if (FootstepAudioClips.Length > 0)
                {
                    var index = Random.Range(0, FootstepAudioClips.Length);
                    AudioSource.PlayClipAtPoint(FootstepAudioClips[index], transform.TransformPoint(_controller.center), FootstepAudioVolume);
                }
            }
        }

        private void OnLand(AnimationEvent animationEvent)
        {
            if (animationEvent.animatorClipInfo.weight > 0.5f)
            {
                AudioSource.PlayClipAtPoint(LandingAudioClip, transform.TransformPoint(_controller.center), FootstepAudioVolume);
            }
        }

    }

    
}