using System;
using System.Collections.Generic;
using System.Linq;
using CitizenFX.Core;
using Roleplay.SharedClasses;
using Newtonsoft.Json;

namespace Roleplay.Server.Classes.Crime
{
	internal static class DrillMiniGame
	{
		private const int NumberOfDrillLocations = 3; 
		private const int DisabledLockValue = 1;
		private const float FinalLockPinPosition = 0.775f;
		private const float InitialHoleDepth = 0.1f;

		private static Dictionary<int, float> _drillLocationLockStatuses;

		public static void Init() {
			InitializeLocationDictionary( NumberOfDrillLocations );

			Server.ActiveInstance.RegisterEventHandler( "DrillMiniGame.GetLockStatus",
				new Action<Player, int>( HandleGetlockStatus ) );

			Server.ActiveInstance.RegisterEventHandler( "DrillMiniGame.SetLockStatus",
				new Action<Player, int, float>( HandleSetLockStatus ) );

			Server.ActiveInstance.RegisterEventHandler( "DrillMiniGame.ResetLockBox",
				new Action<Player, int>( HandleResetLockBox ) );

			Server.ActiveInstance.RegisterEventHandler( "DrillMiniGame.ResetLockBoxes",
				new Action<Player, string>( HandleResetLockBoxes ) );
		}

		/// <summary>
		/// Handles resetting a single lock box.
		/// </summary>
		/// <param name="source">The source.</param>
		/// <param name="locationId">The location identifier.</param>
		private static void HandleResetLockBox( [FromSource] Player source, int locationId) {
			ResetLockBox( locationId );
		}

		/// <summary>
		/// Handles resetting multiple lock boxes.
		/// </summary>
		/// <param name="source">The source.</param>
		/// <param name="data">The data.</param>
		private static void HandleResetLockBoxes( [FromSource] Player source, string data ) {
			var lockBoxIds = JsonConvert.DeserializeObject<List<int>>( data ) ?? new List<int>();

			if( !lockBoxIds.Any() ) return;

			foreach( var boxId in lockBoxIds ) {
				ResetLockBox( boxId );
			}
		}

		/// <summary>
		/// Resets the lock box.
		/// </summary>
		/// <param name="locationId">The location identifier.</param>
		private static void ResetLockBox( int locationId ) {
			if( !_drillLocationLockStatuses.ContainsKey( locationId ) ) {
				Log.Error($"ResetLockBox failed. Location[{locationId}] does not exist.");
				return;
			}

			_drillLocationLockStatuses[locationId] = InitialHoleDepth;
		}

		/// <summary>
		/// Handles the getlock status.
		/// </summary>
		/// <param name="source">The source.</param>
		/// <param name="locationId">The location identifier.</param>
		private static void HandleGetlockStatus( [FromSource] Player source, int locationId ) {
			if( !SessionManager.SessionList.ContainsKey( source.Handle ) ) return;

			if( !_drillLocationLockStatuses.ContainsKey( locationId ) ) return;

			var lockStatus = _drillLocationLockStatuses[locationId];
			source.TriggerEvent( "DrillMiniGame.ReceiveLockStatus", lockStatus );

			SessionManager.SessionList.Where( p => p.Value.IsPlaying && p.Value.Player.Handle != source.Handle ).ToList().ForEach( p =>
				p.Value.TriggerEvent( "DrillMiniGame.AddObservationScene", locationId ) );

			Log.Verbose( $"{source.Name}:Getting lock box[{locationId}] status={lockStatus}" );
		}

		/// <summary>
		/// Handles the update lock status.
		/// </summary>
		/// <param name="source">The source.</param>
		/// <param name="locationId">The location identifier.</param>
		/// <param name="holeDepth">The hole depth.</param>
		private static void HandleSetLockStatus( [FromSource] Player source, int locationId, float holeDepth ) {
			if( !SessionManager.SessionList.ContainsKey( source.Handle ) ) return;

			if( !_drillLocationLockStatuses.ContainsKey( locationId ) ) return;

			_drillLocationLockStatuses[locationId] = holeDepth;

			SessionManager.SessionList.Where( p => p.Value.IsPlaying && p.Value.Player.Handle != source.Handle).ToList().ForEach( p =>
				p.Value.TriggerEvent( "DrillMiniGame.RemoveObservationScene", locationId ) );

			Log.Verbose( $"{source.Name}:Setting lock box[{locationId}] status={holeDepth}" );
		}

		/// <summary>
		///     Builds the location dictionary.
		/// </summary>
		/// <param name="numberOfDrillLocations">The number of drill locations.</param>
		private static void InitializeLocationDictionary( int numberOfDrillLocations ) {
			_drillLocationLockStatuses = new Dictionary<int, float>();
			for( int i = 0; i < numberOfDrillLocations; i++ ) {
				if( _drillLocationLockStatuses.ContainsKey( i ) ) {
					Log.Error( "Error: duplicate ID found in _drillLocationLockStatuses" );
					continue;
				}

				_drillLocationLockStatuses[i] = InitialHoleDepth;
			}
		}

		public class DrillLocationModel
		{
			public int Id { get; set; }
			public float LockStatus { get; set; }
		}
	}
}