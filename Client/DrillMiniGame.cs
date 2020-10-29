using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CitizenFX.Core;
using CitizenFX.Core.Native;
using CitizenFX.Core.UI;
using Common;
using Roleplay.Client.Classes.Inventory;
using Roleplay.Client.Classes.Jobs.Police;
using Roleplay.Client.Classes.Player;
using Roleplay.Client.Helpers;
using Roleplay.Enums;
using Roleplay.Enums.Police;
using Roleplay.SharedClasses;
using Roleplay.SharedModels;
using Newtonsoft.Json;

namespace Roleplay.Client.Classes.Crime.Robberies
{
	internal class DrillMiniGame
	{
		public enum DrillBitMaterialsEnum
		{
			HighSpeedSteel,
			Cobalt,
			Carbide
		}

		private const float DepositBoxLockPositionStart = 0.1f;
		private const string DrillPropName = "hei_prop_heist_drill";
		private const string DrillSpeedDecor = "drillSpeed";
		private const uint DrillHash = 3851537501;

		//TODO: TESTING ONLY REMOVE
		private static bool ForceDrillCamera = true;

		private static float _initPlayerHealth;

		private static Vector4 _currentLocation;

		private static bool _areAssetsInitialized;
		private static bool _miniGameActive;

		private static int _drillScaleform;

		private static int _drillFx;
		private static bool _drillFxActive;

		private static DateTime _lastDrillSpeedUpdate;
		private static float _drillSpeed;
		private static float _drillPosition;
		private static float _drillTemp;

		private static int _locationId;
		private static float _holeDepth;
		private static int _drillPropHandle;

		private static Dictionary<float, bool> _pinPositions;

		private static int _pinSound;
		private static int _drillSound;
		private static int _drillFailSound;
		private static bool _drillSoundActive;

		private static readonly DrillBitMaterialsEnum _drillBitType = DrillBitMaterialsEnum.HighSpeedSteel;

		private static readonly Dictionary<int, ObservedScene> ObservedLocations = new Dictionary<int, ObservedScene>();
		private static bool _observedLocationsLock;
		private static bool _observationAssetsInitialized;

		private static Camera _camera;
		private static bool _cameraInitialized;

		/// <summary>
		///     Initializes this instance.
		/// </summary>
		public static void Init() {
			try {
				Client.ActiveInstance.RegisterTickHandler( SceneObserverTick );
				Client.ActiveInstance.RegisterTickHandler( DrillTempReductionTask );
				//TODO: TESTING ONLY REMOVE
				Client.ActiveInstance.RegisterTickHandler( LocationFinder );

				Client.ActiveInstance.RegisterEventHandler( "DrillMiniGame.ReceiveLockStatus",
					new Action<float>( HandleReceiveLockStatus ) );

				Client.ActiveInstance.RegisterEventHandler( "DrillMiniGame.AddObservationScene",
					new Action<int>( HandleAddObservationScene ) );

				Client.ActiveInstance.RegisterEventHandler( "DrillMiniGame.RemoveObservationScene",
					new Action<int>( HandleRemoveObservationScene ) );

				API.DecorRegister( DrillSpeedDecor, (int)DecorType.DECOR_TYPE_FLOAT );

				//TODO: TESTING ONLY REMOVE
				Client.ActiveInstance.ClientCommands.Register( "/dt", td );
			}
			catch( Exception ex ) {
				Log.Error( ex );
			}
		}

		//TODO: TESTING ONLY REMOVE
		private static async void td( Command command ) {
			try {
				if( bool.TryParse( command.Args.Get( 0 ), out bool result ) ) ForceDrillCamera = result;
			}
			catch( Exception ex ) {
				Log.Error( ex );
			}
		}

		/// <summary>
		///     Runs the drill mini game.
		/// </summary>
		/// <param name="currentLocation">The current location.</param>
		/// <returns></returns>
		public static async Task<bool> RunDrillMiniGame( Vector3 currentLocation ) {
			if( !PlayerHasRequiredTools() ) return false;

			int locationId = GetNearestLocationId( currentLocation );
			if( locationId < 0 ) return false;

			var location = DrillMiniGameLocations.Locations[locationId];

			_locationId = locationId;
			_currentLocation = location;
			BaseScript.TriggerServerEvent( "DrillMiniGame.GetLockStatus", _locationId );

			int success = 0;
			var timeout = DateTime.Now.AddSeconds( 5 );
			while( success == 0 || success == -2 && DateTime.Now.CompareTo( timeout ) < 0 ) {
				success = await RunMiniGameTask();
				await BaseScript.Delay( 0 );
			}

			return success == 1;
		}

		/// <summary>
		///     Uses the drill item.
		/// </summary>
		/// <param name="model">The model.</param>
		/// <returns></returns>
		public static async Task UseDrillItem( InventoryItemsModel model ) {
			string drillItem = "indstdrill";
			string drillBitItem = model.MetaData.ItemKey;

			var itemsToRemove = new[] {drillItem, drillBitItem};

			foreach( string itemToRemove in itemsToRemove ) {
				var item = PlayerInventory.SelectItem( itemToRemove );
				if( item == null ) return;
				item.ItemQuantity = 1;
				BaseScript.TriggerServerEvent( "Player.RemoveItem", JsonConvert.SerializeObject( item ) );
				await BaseScript.Delay( 250 );
			}

			if( !PlayerInventory.AllItems.TryGetValue( "indstdrillandhss", out var drillWithBit ) ) return;
			string data = JsonConvert.SerializeObject( drillWithBit );

			BaseScript.TriggerServerEvent( "Shops.AddItemToInventory", data, 1 );
		}

		/// <summary>
		///     Handles the add observation scene.
		/// </summary>
		/// <param name="locId">The loc identifier.</param>
		private static async void HandleAddObservationScene( int locId ) {
			if( !DrillMiniGameLocations.Locations.ContainsKey( locId ) ) return;

			var timeout = DateTime.Now.AddMinutes( 3 );
			while( _observedLocationsLock && DateTime.Now.CompareTo( timeout ) < 0 ) await BaseScript.Delay( 100 );

			if( _observedLocationsLock ) return;
			Log.Info( $"Adding observation scene={locId}" );
			_observedLocationsLock = true;
			ObservedLocations[locId] = new ObservedScene( locId );
			_observedLocationsLock = false;
		}

		/// <summary>
		///     Handles the remove observation scene.
		/// </summary>
		/// <param name="locId">The loc identifier.</param>
		private static async void HandleRemoveObservationScene( int locId ) {
			if( !ObservedLocations.ContainsKey( locId ) ) return;

			var timeout = DateTime.Now.AddMinutes( 3 );
			while( _observedLocationsLock && DateTime.Now.CompareTo( timeout ) < 0 ) await BaseScript.Delay( 100 );

			if( _observedLocationsLock ) return;
			Log.Info( $"Removing observation scene={locId}" );
			_observedLocationsLock = true;
			var loc = ObservedLocations[locId];
			loc.CleanupSounds();
			loc.ToggleDrillFx( false );
			ObservedLocations.Remove( locId );
			_observedLocationsLock = false;
		}

		/// <summary>
		///     Handles the receive lock status.
		/// </summary>
		/// <param name="holeDepth">The hole depth.</param>
		private static void HandleReceiveLockStatus( float holeDepth ) {
			if( _currentLocation == Vector4.Zero ) return;
			StartMiniGame( holeDepth );
		}

		//TODO: TESTING ONLY REMOVE
		private static async Task LocationFinder() {
			if( !Session.HasJoinedRP || !PlayerHasRequiredTools() ) {
				await BaseScript.Delay( 3015 );
				return;
			}

			foreach( var location in DrillMiniGameLocations.Locations ) {
				if( Cache.PlayerPos.DistanceToSquared( new Vector3( location.Value.X, location.Value.Y,
					    location.Value.Z ) ) > 2f ) continue;
				Screen.DisplayHelpTextThisFrame(
					"Press ~INPUT_PICKUP~ to use a drill here." );

				if( ControlHelper.IsControlJustPressed( Control.Pickup ) ) {
					bool success = await RunDrillMiniGame( CurrentPlayer.Ped.Position );
					Log.ToChat( $"success={success}" );
				}
			}
		}

		/// <summary>
		///     Drill minigame task.
		/// </summary>
		/// <returns></returns>
		private static async Task<int> RunMiniGameTask() {
			try {
				if( _miniGameActive ) {
					if( ShouldGameEndImmediately() ) EndMiniGame( true );
					Screen.DisplayHelpTextThisFrame(
						"Press ~INPUT_PICKUP~ to stop using drill." );
					if( ControlHelper.IsControlJustPressed( Control.Pickup ) ) {
						await BaseScript.Delay( Rand.GetRange( 1000, 2000 ) );
						EndMiniGame( false );

						return _holeDepth > _pinPositions.OrderByDescending( x => x.Key ).FirstOrDefault().Key ? 1 : -1;
					}

					if( !_cameraInitialized && ForceDrillCamera ) {
						var startRot = new Vector3( -29.428f, 1.51f, _currentLocation.W );
						ActivateDrillViewCamera(
							new Vector3( _currentLocation.X, _currentLocation.Y, _currentLocation.Z + 1.6f ),
							startRot );

						_cameraInitialized = true;
					}
					else if( ForceDrillCamera ) {
						PerformCameraShake();
					}

					if( !_areAssetsInitialized ) {
						await RequestAssets();
						await InitializeDrillScaleFormAsync();
						InitializeDrillMiniGameSettings();
						InitializeSoundIds();

						_areAssetsInitialized = true;
					}

					if( _drillPropHandle <= 0 ) _drillPropHandle = await CreateAndAttachDrill();

					DisableControlsPerFrame();
					DrawDrillScaleForm();
					SetDrillPositionAndSpeed();
					DrillAnimation();
					if( IsDrillingHole() ) {
						ProgressDrillThroughHole();
						DidLockPinBreak();
					}
					else {
						if( _drillSpeed > 0 && _drillSoundActive )
							Function.Call( Hash.SET_VARIABLE_ON_SOUND, _drillSound, "DrillState", 0.2f );
					}

					if( DateTime.Now.CompareTo( _lastDrillSpeedUpdate.AddMilliseconds( 500 ) ) >= 0 ) {
						API.DecorSetFloat( _drillPropHandle, DrillSpeedDecor, _drillSpeed );
						_lastDrillSpeedUpdate = DateTime.Now;
					}

					SetDrillSound();
					SetDrillParticleEffects();
					SetDrillTemperature();

					bool isBitBroke = IsDrillBitBroke();
					if( isBitBroke ) {
						API.DecorSetFloat( _drillPropHandle, DrillSpeedDecor, -1 );
						await DrillFailureRoutine();
						API.DecorSetFloat( _drillPropHandle, DrillSpeedDecor, 0 );

						EndMiniGame( false );
						return _holeDepth > _pinPositions.OrderByDescending( x => x.Key ).FirstOrDefault().Key ? 1 : -1;
					}

					return 0;
				}

				return -2;
			}
			catch( Exception ex ) {
				Log.Error( ex );
				return -2;
			}
		}

		/// <summary>
		///     Scene observer tick.
		/// </summary>
		/// <returns></returns>
		private static async Task SceneObserverTick() {
			try {
				if( !Session.HasJoinedRP ) {
					await BaseScript.Delay( 2500 );
					return;
				}

				if( !ObservedLocations.Any() || _miniGameActive ) {
					await BaseScript.Delay( 1000 );
					return;
				}

				var lockTimeout = DateTime.Now.AddSeconds( 10 );
				while( _observedLocationsLock && DateTime.Now.CompareTo( lockTimeout ) < 0 )
					await BaseScript.Delay( 500 );

				if( _observedLocationsLock || !ObservedLocations.Any() ) return;

				var timedOutScenes = new List<int>();
				var nearbyScenes = new List<ObservedScene>();
				var currentTime = DateTime.Now;

				_observedLocationsLock = true;
				foreach( var loc in ObservedLocations ) {
					if( currentTime.CompareTo( loc.Value.SceneTimeout ) >= 0 ) {
						timedOutScenes.Add( loc.Key );
						continue;
					}

					if( loc.Value.Coords == Vector3.Zero ) continue;

					if( Cache.PlayerPos.DistanceToSquared( loc.Value.Coords ) < 250 ) nearbyScenes.Add( loc.Value );
				}

				foreach( int timedOutScene in timedOutScenes )
					if( ObservedLocations.ContainsKey( timedOutScene ) )
						ObservedLocations.Remove( timedOutScene );
				_observedLocationsLock = false;

				if( nearbyScenes.Any() ) {
					if( !_observationAssetsInitialized ) {
						await RequestAssets();
						InitializeSoundIds();
						_observationAssetsInitialized = true;
					}
				}
				else {
					if( _observationAssetsInitialized ) {
						Dispose();
						_observationAssetsInitialized = false;
					}

					await BaseScript.Delay( 500 );
					return;
				}

				foreach( var nearbyScene in nearbyScenes ) {
					if( nearbyScene.DrillPropHandle < 0 ) {
						int drillEntity = API.GetClosestObjectOfType( nearbyScene.Coords.X, nearbyScene.Coords.Y,
							nearbyScene.Coords.Z, 4f, DrillHash,
							false, false, false );

						var entity = Entity.FromHandle( drillEntity );
						if( entity == null || !entity.Exists() ) {
							int prop = Props.FindProps3D( DrillPropName, nearbyScene.Coords, 4f ).FirstOrDefault();
							entity = Entity.FromHandle( prop );
						}

						if( entity == null || !entity.Exists() ) continue;

						nearbyScene.DrillPropHandle = entity.Handle;
					}

					var drillProp = Entity.FromHandle( nearbyScene.DrillPropHandle );
					if( drillProp == null || !drillProp.Exists() ) {
						nearbyScene.DrillPropHandle = -1;
						continue;
					}

					if( DateTime.Now.CompareTo( nearbyScene.LastDrillSpeedPollTime.AddMilliseconds( 500 ) ) >= 0 ) {
						nearbyScene.LastDrillSpeedPollTime = DateTime.Now;

						float drillSpeed = 0.2f;
						if( API.DecorExistOn( nearbyScene.DrillPropHandle, DrillSpeedDecor ) )
							drillSpeed = API.DecorGetFloat( nearbyScene.DrillPropHandle, DrillSpeedDecor );
						//Check for drill failure where speed==-1
						if( drillSpeed < 0 ) nearbyScene.TriggerDrillFailure();

						nearbyScene.DrillSpeed = MathUtil.Clamp( drillSpeed, 0, 1 );
					}

					nearbyScene.ToggleDrillSound( nearbyScene.DrillSpeed );
					bool isDrilling = nearbyScene.DrillSpeed > 0;
					nearbyScene.ToggleDrillFx( isDrilling );
				}
			}
			catch( Exception ex ) {
				Log.Error( ex );
			}
		}

		/// <summary>
		///     Drills the temporary reduction task.
		/// </summary>
		/// <returns></returns>
		private static async Task DrillTempReductionTask() {
			if( !Session.HasJoinedRP || !PlayerHasRequiredTools() ) {
				await BaseScript.Delay( 3015 );
				return;
			}

			if( !_miniGameActive || _drillSpeed > 0 || IsDrillingHole() || _drillTemp <= 0 ) {
				await BaseScript.Delay( 250 );
				return;
			}

			CalcIdleDrillTempReduction();

			await BaseScript.Delay( 0 );
		}

		/// <summary>
		///     Calculates the idle drill temporary reduction.
		/// </summary>
		private static void CalcIdleDrillTempReduction() {
			float tempReduction = (_drillSpeed > 0 ? Rand.GetRange( 50, 101 ) : Rand.GetRange( 250, 501 )) / 1000000f;
			if( _drillTemp > 0 ) _drillTemp = _drillTemp - tempReduction;

			if( _drillTemp < 0 ) _drillTemp = 0;
		}

		/// <summary>
		///     Starts the mini game.
		/// </summary>
		/// <param name="initHoleDepth">The initialize hole depth.</param>
		private static async void StartMiniGame( float initHoleDepth ) {
			ResetPinPositions( initHoleDepth );

			CurrentPlayer.Ped.Position = new Vector3( _currentLocation.X, _currentLocation.Y, _currentLocation.Z );
			await BaseScript.Delay( 100 );
			CurrentPlayer.Ped.Heading = _currentLocation.W;
			CurrentPlayer.EnableWeaponWheel( false );
			CurrentPlayer.Ped.IsPositionFrozen = true;
			_initPlayerHealth = Cache.PlayerHealth;

			_miniGameActive = true;
		}

		/// <summary>
		///     Ends the mini game.
		/// </summary>
		/// <param name="endImmediately">if set to <c>true</c> [end immediately].</param>
		private static async void EndMiniGame( bool endImmediately ) {
			_miniGameActive = false;
			DeactivateDrillViewCamera();
			//TODO: Testing only, uncomment when server (cop?) reset implemented.
			//BaseScript.TriggerServerEvent( "DrillMiniGame.SetLockStatus", _locationId, _holeDepth );
			StopDrillSound( _drillSound );

			CurrentPlayer.EnableWeaponWheel( true );
			CurrentPlayer.Ped.IsPositionFrozen = false;
			await BaseScript.Delay( 100 );

			if( endImmediately ) {
				CurrentPlayer.Ped.Task.ClearAllImmediately();
			}
			else {
				CurrentPlayer.Ped.Task.ClearAnimation( "anim@heists@fleeca_bank@drilling", "drill_straight_start" );
				CurrentPlayer.Ped.Task.ClearAll();
				CurrentPlayer.Ped.Task.PlayAnimation( "anim@heists@fleeca_bank@drilling", "drill_straight_idle", 8f,
					500,
					(AnimationFlags)33 );
				await BaseScript.Delay( 1000 );
				CurrentPlayer.Ped.Task.ClearAnimation( "anim@heists@fleeca_bank@drilling", "drill_straight_idle" );
				CurrentPlayer.Ped.Task.ClearAll();
			}

			Dispose();

			_currentLocation = Vector4.Zero;
			_drillPropHandle = -1;
		}

		/// <summary>
		///     Should the mini game be terminated.
		/// </summary>
		/// <returns></returns>
		private static bool ShouldGameEndImmediately() {
			int currentHealth = Cache.PlayerHealth;
			if( currentHealth > _initPlayerHealth ) _initPlayerHealth = currentHealth;

			bool isDead = CurrentPlayer.Ped.IsDead ||
			              Function.Call<bool>( Hash.DECOR_GET_BOOL, Cache.PlayerHandle, "Ped.IsIncapacitated" );
			bool endImmediately = Arrest.PlayerCuffState != CuffState.None || CurrentPlayer.Ped.IsRagdoll ||
			                      currentHealth < _initPlayerHealth || isDead;

			return endImmediately;
		}

		/// <summary>
		///     Gets the nearest location identifier.
		/// </summary>
		/// <param name="currentLocation">The current location.</param>
		/// <returns></returns>
		private static int GetNearestLocationId( Vector3 currentLocation ) {
			foreach( var location in DrillMiniGameLocations.Locations ) {
				if( currentLocation.DistanceToSquared( new Vector3( location.Value.X, location.Value.Y,
					    location.Value.Z ) ) > 2f ) continue;
				return location.Key;
			}

			return -1;
		}

		/// <summary>
		///     Resets the pin positions.
		/// </summary>
		/// <param name="holeDepth">The hole depth.</param>
		private static void ResetPinPositions( float holeDepth ) {
			_pinPositions = new Dictionary<float, bool>();

			var pinPositions = new[] {0.325f, 0.475f, 0.625f, 0.775f};
			foreach( float position in pinPositions ) {
				bool isPinActive = holeDepth > position;
				_pinPositions[position] = isPinActive;
			}

			_holeDepth = holeDepth;
		}

		/// <summary>
		///     Activates the drill view camera.
		/// </summary>
		/// <param name="position">The position.</param>
		/// <param name="rotation">The rotation.</param>
		private static async void ActivateDrillViewCamera( Vector3 position, Vector3 rotation ) {
			if( _camera != null ) {
				API.DestroyCam( _camera.Handle, false );
				_camera = null;
			}

			API.DoScreenFadeOut( 500 );
			await BaseScript.Delay( 500 );

			API.SetFocusArea( position.X, position.Y, position.Z, 0, 0, 0 );

			const float defaultFov = 30;
			_camera = World.CreateCamera( position, rotation, defaultFov );

			World.RenderingCamera = _camera;
			_camera.FarDepthOfField = 0;
			_camera.NearDepthOfField = 0;
			_camera.DepthOfFieldStrength = 0;

			API.DoScreenFadeIn( 500 );
		}

		/// <summary>
		///     Deactivates the drill view camera.
		/// </summary>
		private static async void DeactivateDrillViewCamera() {
			if( _camera == null ) return;

			API.DoScreenFadeOut( 500 );
			await BaseScript.Delay( 500 );

			API.DestroyCam( _camera.Handle, false );
			_camera?.Delete();
			_camera = null;
			API.ClearTimecycleModifier();
			API.SetFocusEntity( CurrentPlayer.Ped.Handle );
			World.RenderingCamera = null;

			API.DoScreenFadeIn( 500 );

			_cameraInitialized = false;
		}

		/// <summary>
		///     Performs the camera shake.
		/// </summary>
		private static void PerformCameraShake() {
			if( _camera == null || !(_drillPosition >= DepositBoxLockPositionStart) || !(_drillSpeed > 0) ) return;

			if( Rand.GetScalar() > 0.925f )
				_camera.Shake( CameraShake.Vibrate, _drillSpeed );
			else
				_camera.Shake( CameraShake.Jolt, Rand.GetRange( 600, 1001 ) / 1000f );
		}

		/// <summary>
		///     Players has required tools.
		/// </summary>
		/// <returns>Whether play has drill with attached bit</returns>
		private static bool PlayerHasRequiredTools() {
			if( PlayerInventory.CurrentPlayerInventory == null ||
			    PlayerInventory.CurrentPlayerInventory.Items == null ) return false;

			bool hasDrill =
				PlayerInventory.CurrentPlayerInventory.Items.Exists( i =>
					i.MetaData.ItemKey.StartsWith( "indstdrilland" ) );
			return hasDrill;
		}

		/// <summary>
		///     Requests the assets.
		/// </summary>
		/// <returns></returns>
		private static async Task RequestAssets() {
			API.RequestAnimDict( "anim@heists@fleeca_bank@drilling" );
			API.RequestNamedPtfxAsset( "fm_mission_controler" );

			int timeout = 10;
			while( timeout > 0 && (!API.HasAnimDictLoaded( "anim@heists@fleeca_bank@drilling" ) ||
			                       !API.HasNamedPtfxAssetLoaded( "fm_mission_controler" ) ||
			                       !API.RequestAmbientAudioBank( "DLC_HEIST_FLEECA_SOUNDSET", false ) ||
			                       !API.RequestMissionAudioBank( "DLC_HEIST_FLEECA_SOUNDSET", false ) ||
			                       !API.RequestScriptAudioBank( "DLC_HEIST_FLEECA_SOUNDSET", false ) ||
			                       !API.RequestAmbientAudioBank( "HEIST_FLEECA_DRILL", false ) ||
			                       !API.RequestAmbientAudioBank( "HEIST_FLEECA_DRILL_2", false )) ||
			       !API.RequestAmbientAudioBank( "DLC_MPHEIST\\HEIST_FLEECA_DRILL", false ) ||
			       !API.RequestAmbientAudioBank( "DLC_MPHEIST\\HEIST_FLEECA_DRILL_2", false )
			) {
				await BaseScript.Delay( 100 );
				timeout = timeout - 1;
			}
		}

		/// <summary>
		///     Initializes the drill mini game settings.
		/// </summary>
		private static void InitializeDrillMiniGameSettings() {
			SetDrillSpeed( 0 );
			SetHoleDepth( _holeDepth );
			SetDrillPosition( 0 );
			SetDrillTemperature();
		}

		/// <summary>
		///     Initializes the drill scale form asynchronous.
		/// </summary>
		/// <returns></returns>
		private static async Task InitializeDrillScaleFormAsync() {
			int scale = API.RequestScaleformMovieInstance( "DRILLING" );
			int timeout = 100;
			while( !API.HasScaleformMovieLoaded( scale ) && timeout > 0 ) {
				await BaseScript.Delay( 100 );
				timeout = timeout - 1;
			}

			_drillScaleform = scale;
		}

		/// <summary>
		///     Initializes the sound ids.
		/// </summary>
		private static void InitializeSoundIds() {
			_pinSound = API.GetSoundId();
			_drillSound = API.GetSoundId();
			_drillFailSound = API.GetSoundId();
		}

		/// <summary>
		///     Draws the drill scale form.
		/// </summary>
		private static void DrawDrillScaleForm() {
			API.DrawScaleformMovieFullscreen( _drillScaleform, 255, 255, 255, 255, 0 );
		}

		/// <summary>
		///     Creates and attaches drill prop.
		/// </summary>
		/// <returns></returns>
		private static async Task<int> CreateAndAttachDrill() {
			try {
				var model = new Model( DrillPropName );
				await model.Request( 250 );

				if( !model.IsInCdImage || !model.IsValid ) return -1;

				while( !model.IsLoaded ) await BaseScript.Delay( 10 );

				var offsetPosition = CurrentPlayer.Ped.GetOffsetPosition( Vector3.One );
				var attachPosition = API.GetPedBoneCoords( Cache.PlayerHandle, (int)Bone.SKEL_R_Hand, offsetPosition.X,
					offsetPosition.Y,
					offsetPosition.Z );

				var prop = await World.CreateProp( model, attachPosition, new Vector3( 0, 0, 0 ),
					false, false );
				prop.IsPositionFrozen = true;
				prop.IsCollisionEnabled = false;
				API.AttachEntityToEntity( prop.Handle, Cache.PlayerHandle,
					API.GetPedBoneIndex( Cache.PlayerHandle, (int)Bone.PH_R_Hand ), 0.1f, 0, 0, 0,
					90f, 0,
					true,
					true, false, true, 1, true );
				prop.IsInvincible = true;

				model.MarkAsNoLongerNeeded();

				return prop.Handle;
			}
			catch( Exception ex ) {
				Log.Error( ex );
				return -1;
			}
		}

		/// <summary>
		///     Toggle drill sound status
		/// </summary>
		/// <param name="drillSpeed">The drill speed.</param>
		/// <param name="entityHandle">The entity handle.</param>
		/// <param name="soundId">The sound identifier.</param>
		private static void ToggleDrillSoundStatus( float drillSpeed, int entityHandle, int soundId ) {
			if( drillSpeed > 0 && !_drillSoundActive ) {
				API.PlaySoundFromEntity( soundId, "Drill", entityHandle, "DLC_HEIST_FLEECA_SOUNDSET", false, 0 );
				_drillSoundActive = true;
			}
			else {
				if( drillSpeed <= 0 ) StopDrillSound( soundId );
			}
		}

		/// <summary>
		///     Stops the drill sound.
		/// </summary>
		/// <param name="soundId">The sound identifier.</param>
		private static void StopDrillSound( int soundId ) {
			if( soundId > 0 && !API.HasSoundFinished( soundId ) ) API.StopSound( soundId );
			_drillSoundActive = false;
		}

		/// <summary>
		///     Sets the drill position and speed.
		/// </summary>
		private static void SetDrillPositionAndSpeed() {
			//Mouse up and down (cam)

			float mouse = API.GetControlNormal( 2, 240 );

			float position = 0.9f - mouse;

			if( position > 0 ) {
				if( position > _holeDepth ) position = _holeDepth;
				SetDrillPosition( position );
			}

			//Wheel up
			if( API.IsDisabledControlJustPressed( 2, 241 ) ) {
				if( _drillSpeed < 1.0f )
					_drillSpeed = _drillSpeed + 0.125f;
				else
					_drillSpeed = 1.0f;
			}
			//Wheel down
			else if( API.IsDisabledControlJustPressed( 2, 242 ) ) {
				if( _drillSpeed > 0f )
					_drillSpeed = _drillSpeed - 0.125f;
				else
					_drillSpeed = 0f;
			}

			SetDrillSpeed( _drillSpeed );
		}

		/// <summary>
		///     Sets the drill position.
		/// </summary>
		/// <param name="position">The position.</param>
		private static void SetDrillPosition( float position ) {
			_drillPosition = position;
			API.CallScaleformMovieFunctionFloatParams( _drillScaleform, "SET_DRILL_POSITION", _drillPosition,
				-1082130432, -1082130432,
				-1082130432, -1082130432 );
		}

		/// <summary>
		///     Sets the hole depth.
		/// </summary>
		/// <param name="holeDepth">The hole depth.</param>
		private static void SetHoleDepth( float holeDepth ) {
			_holeDepth = holeDepth;
			API.CallScaleformMovieFunctionFloatParams( _drillScaleform, "SET_HOLE_DEPTH", _holeDepth, -1082130432,
				-1082130432,
				-1082130432, -1082130432 );
		}

		/// <summary>
		///     Sets the drill speed.
		/// </summary>
		/// <param name="speed">The speed.</param>
		private static void SetDrillSpeed( float speed ) {
			_drillSpeed = speed;
			API.CallScaleformMovieFunctionFloatParams( _drillScaleform, "SET_SPEED", _drillSpeed, -1082130432,
				-1082130432,
				-1082130432, -1082130432 );

			ToggleDrillSoundStatus( _drillSpeed, _drillPropHandle, _drillSound );
		}

		/// <summary>
		///     Sets the drill temperature.
		/// </summary>
		private static void SetDrillTemperature() {
			API.CallScaleformMovieFunctionFloatParams( _drillScaleform, "SET_TEMPERATURE", _drillTemp, -1082130432,
				-1082130432,
				-1082130432, -1082130432 );
		}

		/// <summary>
		///     Sets the drill sound.
		/// </summary>
		private static void SetDrillSound() {
			float drillVolume;
			if( _drillSpeed > 0f && _drillSpeed <= 0.2f )
				drillVolume = 0.3f;
			else if( _drillSpeed > 0.2f && _drillSpeed <= 0.4f )
				drillVolume = 0.4f;
			else if( _drillSpeed > 0.4f && _drillSpeed <= 0.6f )
				drillVolume = 0.6f;
			else if( _drillSpeed > 0.6f && _drillSpeed <= 0.8f )
				drillVolume = 0.8f;
			else
				drillVolume = 1f;
			if( drillVolume > 0 ) Function.Call( Hash.SET_VARIABLE_ON_SOUND, _drillSound, "DrillState", drillVolume );
		}

		/// <summary>
		///     Sets the drill particle effects.
		/// </summary>
		private static void SetDrillParticleEffects() {
			if( _drillSpeed <= 0 || _drillPosition < DepositBoxLockPositionStart ) {
				if( _drillFxActive ) ToggleDrillParticleFx( false, _drillPropHandle, ref _drillFx );
				_drillFxActive = false;
			}
			else if( _drillSpeed > 0 && _drillPosition >= DepositBoxLockPositionStart ) {
				if( !_drillFxActive ) ToggleDrillParticleFx( true, _drillPropHandle, ref _drillFx );
				_drillFxActive = true;
			}
		}

		/// <summary>
		///     Progresses the drill through hole.
		/// </summary>
		private static void ProgressDrillThroughHole() {
			if( _drillSpeed > 0f && _drillSpeed <= 0.2f ) {
				_holeDepth = _holeDepth + Rand.GetRange( 10, 21 ) / 1000000f;
				_drillTemp = _drillTemp + Rand.GetRange( 50, 75 ) / 1000000f;
			}
			else if( _drillSpeed > 0.2f && _drillSpeed <= 0.4f ) {
				_holeDepth = _holeDepth + Rand.GetRange( 20, 41 ) / 1000000f;
				_drillTemp = _drillTemp + Rand.GetRange( 75, 101 ) / 1000000f;
			}
			else if( _drillSpeed > 0.4f && _drillSpeed <= 0.6f ) {
				_holeDepth = _holeDepth + Rand.GetRange( 40, 156 ) / 1000000f;
				_drillTemp = _drillTemp + Rand.GetRange( 151, 251 ) / 1000000f;
			}
			else if( _drillSpeed > 0.6f && _drillSpeed <= 0.8f ) {
				_holeDepth = _holeDepth + Rand.GetRange( 60, 225 ) / 1000000f;
				_drillTemp = _drillTemp + Rand.GetRange( 225, 501 ) / 1000000f;
			}
			else {
				_holeDepth = _holeDepth + Rand.GetRange( 80, 350 ) / 1000000f;
				_drillTemp = _drillTemp + Rand.GetRange( 700, 1001 ) / 1000000f;
			}

			float drillBitBonus = CalculateDrillBitBonusProgress();
			if( drillBitBonus > 0 ) _holeDepth = _holeDepth + drillBitBonus;
		}

		/// <summary>
		///     Calculates the drill bit bonus progress.
		/// </summary>
		/// <returns></returns>
		private static float CalculateDrillBitBonusProgress() {
			float drillBitBonus = 0f;

			switch( _drillBitType ) {
			case DrillBitMaterialsEnum.HighSpeedSteel:
				break;
			case DrillBitMaterialsEnum.Cobalt:
				//TODO: Placeholder.  Should affect temp, progress, or both?
				drillBitBonus = Rand.GetRange( 10, 51 ) / 1000000f;
				break;
			case DrillBitMaterialsEnum.Carbide:
				//TODO: Placeholder.  Should affect temp, progress, or both?
				drillBitBonus = Rand.GetRange( 50, 151 ) / 1000000f;
				break;
			}

			return drillBitBonus;
		}

		/// <summary>
		///     Determines whether [is drilling hole].
		/// </summary>
		/// <returns>
		///     <c>true</c> if [is drilling hole]; otherwise, <c>false</c>.
		/// </returns>
		private static bool IsDrillingHole() {
			return Math.Abs( _drillPosition - _holeDepth ) < 0.001f && _drillSpeed > 0;
		}

		/// <summary>
		///     Checks if pin broke.
		/// </summary>
		private static void DidLockPinBreak() {
			var updates = new List<float>();
			foreach( var pin in _pinPositions ) {
				bool isPinBroke = pin.Value;
				if( _holeDepth > pin.Key && !isPinBroke ) {
					API.PlaySoundFrontend( _pinSound, "Drill_Pin_Break", "DLC_HEIST_FLEECA_SOUNDSET", true );
					if( _camera != null ) _camera.Shake( CameraShake.Jolt, 1f );
					updates.Add( pin.Key );
				}
			}

			foreach( float update in updates ) _pinPositions[update] = true;
		}

		/// <summary>
		///     Determines whether [is drill bit broke].
		/// </summary>
		/// <returns>
		///     <c>true</c> if [is drill bit broke]; otherwise, <c>false</c>.
		/// </returns>
		private static bool IsDrillBitBroke() {
			if( _drillTemp <= 0.999f || !IsDrillingHole() ) return false;
			return true;
		}

		/// <summary>
		///     Perform drill failure routine.
		/// </summary>
		/// <returns></returns>
		private static async Task DrillFailureRoutine() {
			int waitTime = Rand.GetRange( 5000, 10000 );

			CurrentPlayer.Ped.Task.ClearAnimation( "anim@heists@fleeca_bank@drilling", "drill_straight_start" );
			CurrentPlayer.Ped.Task.ClearAnimation( "anim@heists@fleeca_bank@drilling", "drill_straight_idle" );
			await BaseScript.Delay( 50 );

			CurrentPlayer.Ped.Task.PlayAnimation( "anim@heists@fleeca_bank@drilling", "drill_straight_fail", 8f,
				waitTime,
				(AnimationFlags)33 );

			StopDrillSound( _drillSound );

			API.PlaySoundFrontend( _drillFailSound, "Drill_Jam", "DLC_HEIST_FLEECA_SOUNDSET", true );
			ToggleDrillParticleFx( false, _drillPropHandle, ref _drillFx );
			await BaseScript.Delay( waitTime );

			API.StopSound( _drillFailSound );
			ResetDrill();
		}

		/// <summary>
		///     Resets the drill.
		/// </summary>
		private static void ResetDrill() {
			SetDrillPosition( 0 );
			SetDrillSpeed( 0 );
			_drillTemp = 0;
			SetDrillTemperature();
		}

		/// <summary>
		///     Perform drill animation.
		/// </summary>
		private static void DrillAnimation() {
			if( _drillSpeed <= 0 && !API.IsEntityPlayingAnim( Cache.PlayerHandle, "anim@heists@fleeca_bank@drilling",
				    "drill_straight_idle",
				    3 ) ) {
				CurrentPlayer.Ped.Task.ClearAnimation( "anim@heists@fleeca_bank@drilling", "drill_straight_start" );
				CurrentPlayer.Ped.Task.PlayAnimation( "anim@heists@fleeca_bank@drilling", "drill_straight_idle", 8f, -1,
					(AnimationFlags)33 );
			}
			else if( _drillSpeed > 0 && !API.IsEntityPlayingAnim( Cache.PlayerHandle,
				         "anim@heists@fleeca_bank@drilling", "drill_straight_start",
				         3 ) ) {
				CurrentPlayer.Ped.Task.ClearAnimation( "anim@heists@fleeca_bank@drilling", "drill_straight_idle" );
				CurrentPlayer.Ped.Task.PlayAnimation( "anim@heists@fleeca_bank@drilling", "drill_straight_start", 8f,
					-1,
					(AnimationFlags)33 );
			}
		}

		/// <summary>
		///     Toggles drill particle effects.
		/// </summary>
		/// <param name="isDrilling">if set to <c>true</c> [is drilling].</param>
		/// <param name="drillPropHandle">The drill property handle.</param>
		/// <param name="drillFx">The drill fx.</param>
		private static void ToggleDrillParticleFx( bool isDrilling, int drillPropHandle, ref int drillFx ) {
			if( isDrilling ) {
				API.SetPtfxAssetNextCall( "fm_mission_controler" );
				drillFx = API.StartParticleFxLoopedOnEntity_2( "scr_drill_debris", drillPropHandle, 0.0f, -0.55f,
					.01f, 90.0f, 90.0f, 90.0f, .8f, false, false, false );
				API.SetParticleFxLoopedEvolution( drillFx, "power", Rand.GetRange( 500, 800 ) / 1000f, false );
			}
			else {
				Function.Call( Hash.REMOVE_PARTICLE_FX, drillFx, false );
			}
		}

		/// <summary>
		///     Disables the controls per frame.
		/// </summary>
		private static void DisableControlsPerFrame() {
			API.DisableControlAction( 2, 241, true ); //MwheelUp
			API.DisableControlAction( 2, 242, true ); //MwheelDown
			API.DisableControlAction( 2, 14, true ); //MwheelDown
			API.DisableControlAction( 2, 16, true ); //MwheelDown
			API.DisableControlAction( 2, 15, true ); //MwheelUp
			API.DisableControlAction( 2, 17, true ); //MwheelUp
			API.DisableControlAction( 2, 27, true ); //Phone

			DisableSelectControls();
			DisableAttackControls();
		}

		/// <summary>
		///     Disable weapon switching controls
		/// </summary>
		private static void DisableSelectControls() {
			try {
				API.DisableControlAction( 2, (int)Control.SelectWeapon, true );
				API.DisableControlAction( 2, (int)Control.SelectWeaponUnarmed, true );
				API.DisableControlAction( 2, (int)Control.SelectWeaponMelee, true );
				API.DisableControlAction( 2, (int)Control.SelectWeaponHandgun, true );
				API.DisableControlAction( 2, (int)Control.SelectWeaponShotgun, true );
				API.DisableControlAction( 2, (int)Control.SelectWeaponSmg, true );
			}
			catch( Exception ex ) {
				Log.Error( ex );
			}
		}

		/// <summary>
		///     Disable all attack controls
		/// </summary>
		private static void DisableAttackControls() {
			try {
				API.DisableControlAction( 2, (int)Control.Attack, true );
				API.DisableControlAction( 2, (int)Control.Attack2, true );
				API.DisableControlAction( 2, (int)Control.VehicleDriveLook, true );
				API.DisableControlAction( 2, (int)Control.MeleeAttack1, true );
				API.DisableControlAction( 2, (int)Control.MeleeAttack2, true );
				API.DisableControlAction( 2, (int)Control.MeleeAttackAlternate, true );
				API.DisableControlAction( 2, (int)Control.MeleeAttackHeavy, true );
				API.DisableControlAction( 2, (int)Control.MeleeAttackLight, true );
			}
			catch( Exception ex ) {
				Log.Error( ex );
			}
		}

		/// <summary>
		///     Releases unmanaged and - optionally - managed resources.
		/// </summary>
		private static void Dispose() {
			DisposeDrillProp();
			DisposeOfAnimationDictionary();
			DisposeOfScaleform();
			DisposeParticleFx();
			DisposeOfSoundIds( new List<int> {_pinSound, _drillSound} );
			DisposeAnimDict( new List<string> {"anim@heists@fleeca_bank@drilling"} );

			_areAssetsInitialized = false;
		}

		/// <summary>
		///     Disposes the drill property.
		/// </summary>
		private static void DisposeDrillProp() {
			var prop = Entity.FromHandle( _drillPropHandle );
			if( prop == null || !prop.Exists() ) return;

			prop.Detach();
			prop.Position = new Vector3( -1705.096f, -5812.861f, 0f );
			prop.IsPersistent = false;
			prop.MarkAsNoLongerNeeded();
			prop.Delete();
		}

		/// <summary>
		///     Disposes the particle fx.
		/// </summary>
		private static void DisposeParticleFx() {
			API.RemoveNamedPtfxAsset( "fm_mission_controler" );
		}

		/// <summary>
		///     Disposes the of scaleform.
		/// </summary>
		private static void DisposeOfScaleform() {
			int d = _drillScaleform;
			API.SetScaleformMovieAsNoLongerNeeded( ref d );
		}

		/// <summary>
		///     Disposes the of animation dictionary.
		/// </summary>
		private static void DisposeOfAnimationDictionary() {
			API.RemoveAnimDict( "anim@heists@fleeca_bank@drilling" );
		}

		/// <summary>
		///     Disposes the of sound ids.
		/// </summary>
		/// <param name="soundIds">The sound ids.</param>
		private static void DisposeOfSoundIds( List<int> soundIds ) {
			foreach( int id in soundIds ) API.ReleaseSoundId( id );
		}

		/// <summary>
		///     Disposes the anim dictionary.
		/// </summary>
		/// <param name="animDictionaries">The anim dictionaries.</param>
		private static void DisposeAnimDict( List<string> animDictionaries ) {
			foreach( string dictionary in animDictionaries ) API.RemoveAnimDict( dictionary );
		}

		/// <summary>
		///     Class used for when player observes drill minigame
		/// </summary>
		private class ObservedScene
		{
			private bool _drillFxActive;

			private bool _drillSoundActive;

			public ObservedScene( int locId ) {
				Coords = DrillMiniGameLocations.Locations.ContainsKey( locId )
					? new Vector3( DrillMiniGameLocations.Locations[locId].X, DrillMiniGameLocations.Locations[locId].Y,
						DrillMiniGameLocations.Locations[locId].Z )
					: Vector3.Zero;
				SceneTimeout = DateTime.Now.AddMinutes( 20 );
				DrillPropHandle = -1;
				DrillSound = API.GetSoundId();
				DrillFailSound = API.GetSoundId();
				DrillSpeed = 0;
				LastDrillSpeedPollTime = DateTime.MinValue;
			}

			public Vector3 Coords { get; }
			public DateTime SceneTimeout { get; }
			public int DrillPropHandle { get; set; }
			public int DrillFx { get; set; }
			public int DrillSound { get; }
			public int DrillFailSound { get; }
			public float DrillSpeed { get; set; }
			public DateTime LastDrillSpeedPollTime { get; set; }

			~ObservedScene() {
				try {
					CleanupSounds();
					ToggleDrillFx( false );
				}
				catch( Exception ex ) {
					Log.Error( ex );
				}
			}

			public void CleanupSounds() {
				var sounds = new[] {DrillSound, DrillFailSound};
				foreach( int sound in sounds ) {
					if( sound <= 0 ) continue;
					if( !API.HasSoundFinished( sound ) ) API.StopSound( sound );

					API.ReleaseSoundId( sound );
				}
			}

			public void ToggleDrillFx( bool isDrilling ) {
				if( DrillPropHandle < 0 ) return;

				int drillFx = DrillFx;
				if( isDrilling && !_drillFxActive || !isDrilling ) {
					ToggleDrillParticleFx( isDrilling, DrillPropHandle, ref drillFx );
					_drillFxActive = isDrilling;
				}

				DrillFx = drillFx;
			}

			public void ToggleDrillSound( float drillSpeed ) {
				if( DrillPropHandle < 0 ) return;

				if( drillSpeed > 0 ) {
					if( !_drillSoundActive ) {
						API.PlaySoundFromEntity( DrillSound, "Drill", DrillPropHandle, "DLC_HEIST_FLEECA_SOUNDSET",
							false, 0 );
						_drillSoundActive = true;
					}

					Function.Call( Hash.SET_VARIABLE_ON_SOUND, DrillSound, "DrillState", drillSpeed );
				}
				else if( drillSpeed <= 0 && _drillSoundActive ) {
					StopDrillSound();
					_drillSoundActive = false;
				}
			}

			public async void TriggerDrillFailure() {
				StopDrillSound();

				API.PlaySoundFrontend( DrillFailSound, "Drill_Jam", "DLC_HEIST_FLEECA_SOUNDSET", true );
				await BaseScript.Delay( 1000 );

				if( !API.HasSoundFinished( DrillFailSound ) ) API.StopSound( DrillFailSound );
			}

			public void StopDrillSound() {
				if( !API.HasSoundFinished( DrillSound ) ) API.StopSound( DrillSound );
			}
		}
	}
}