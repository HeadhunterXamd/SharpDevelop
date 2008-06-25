﻿// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="David Srbecký" email="dsrbecky@gmail.com"/>
//     <version>$Revision$</version>
// </file>

using System;
using System.Threading;

namespace Debugger
{
	internal enum DebuggeeStateAction { Keep, Clear }
	
	public partial class Process
	{
		// Order:
		//  Update PauseSession
		//  Update DebugSession
		//  Raise event
		
		#region Events
		
		public event EventHandler<ProcessEventArgs> Paused;
		public event EventHandler<ProcessEventArgs> Resumed;
		
		// HACK: public
		public virtual void OnPaused()
		{
			AssertPaused();
			// No real purpose
			if (callbackInterface.IsInCallback) throw new DebuggerException("Can not raise event within callback.");
			TraceMessage ("Debugger event: OnPaused()");
			if (Paused != null) {
				foreach(Delegate d in Paused.GetInvocationList()) {
					if (IsRunning) {
						TraceMessage ("Skipping OnPaused delegate becuase process has resumed");
						break;
					}
					d.DynamicInvoke(this, new ProcessEventArgs(this));
				}
			}
		}
		
		protected virtual void OnResumed()
		{
			AssertRunning();
			// No real purpose
			if (callbackInterface.IsInCallback) throw new DebuggerException("Can not raise event within callback.");
			TraceMessage ("Debugger event: OnResumed()");
			if (Resumed != null) {
				Resumed(this, new ProcessEventArgs(this));
			}
		}
		
		
		#endregion
		
		#region PauseSession & DebugeeState
		
		PauseSession pauseSession;
		DebuggeeState debuggeeState;
		
		/// <summary>
		/// Indentification of the current debugger session. This value changes whenever debugger is continued
		/// </summary>
		public PauseSession PauseSession {
			get {
				return pauseSession;
			}
		}
		
		/// <summary>
		/// Indentification of the state of the debugee. This value changes whenever the state of the debugee significatntly changes
		/// </summary>
		public DebuggeeState DebuggeeState {
			get {
				return debuggeeState;
			}
		}
		
		/// <summary> Puts the process into a paused state </summary>
		internal void NotifyPaused(PausedReason pauseReason)
		{
			AssertRunning();
			pauseSession = new PauseSession(pauseReason);
			if (debuggeeState == null) {
				debuggeeState = new DebuggeeState(this);
			}
		}
		
		/// <summary> Puts the process into a resumed state </summary>
		internal void NotifyResumed(DebuggeeStateAction action)
		{
			AssertPaused();
			PauseSession oldPauseSession = pauseSession;
			pauseSession = null;
			oldPauseSession.NotifyHasExpired();
			if (action == DebuggeeStateAction.Clear) {
				if (debuggeeState == null) throw new DebuggerException("Debugee state already cleared");
				DebuggeeState oldDebugeeState = debuggeeState;
				debuggeeState = null;
				oldDebugeeState.NotifyHasExpired();
			}
		}
		
		/// <summary> Sets up the eviroment and raises user events </summary>
		internal void RaisePausedEvents()
		{
			DisableAllSteppers();
			SelectMostRecentStackFrameWithLoadedSymbols();
			
			if (this.PauseSession.PausedReason == PausedReason.Exception) {
				ExceptionEventArgs args = new ExceptionEventArgs(this, this.SelectedThread.CurrentException, this.SelectedThread.CurrentExceptionType, this.SelectedThread.CurrentExceptionIsUnhandled);
				OnExceptionThrown(args);
				// The event could have resumed or killed the process
			}
			
			if (this.IsPaused && !this.HasExpired) {
				OnPaused();
				// The event could have resumed the process
			}
		}
		
		#endregion
		
		#region Exceptions
		
		bool pauseOnHandledException = false;
		
		public event EventHandler<ExceptionEventArgs> ExceptionThrown;
		
		public bool PauseOnHandledException {
			get {
				return pauseOnHandledException;
			}
			set {
				pauseOnHandledException = value;
			}
		}
		
		protected internal virtual void OnExceptionThrown(ExceptionEventArgs e)
		{
			TraceMessage ("Debugger event: OnExceptionThrown()");
			if (ExceptionThrown != null) {
				ExceptionThrown(this, e);
			}
		}
		
		#endregion
		
		internal void AssertPaused()
		{
			if (IsRunning) {
				throw new DebuggerException("Process is not paused.");
			}
		}
		
		internal void AssertRunning()
		{
			if (IsPaused) {
				throw new DebuggerException("Process is not running.");
			}
		}
		
		public bool IsRunning { 
			get {
				return pauseSession == null;
			}
		}
		
		public bool IsPaused {
			get {
				return !IsRunning;
			}
		}
		
		public void Break()
		{
			AssertRunning();
			
			corProcess.Stop(uint.MaxValue); // Infinite; ignored anyway
			
			NotifyPaused(PausedReason.ForcedBreak);
			RaisePausedEvents();
		}
		
		#region Convenience methods
		
		public void Continue()
		{
			AsyncContinue();
			WaitForPause();
		}
		
		#endregion
		
		public void AsyncContinue()
		{
			AsyncContinue(DebuggeeStateAction.Clear);
		}
		
		internal void AsyncContinue(DebuggeeStateAction action)
		{
			AssertPaused();
			
			NotifyResumed(action);
			corProcess.Continue(0);
			
			if (action == DebuggeeStateAction.Clear) {
				OnResumed();
			}
		}
		
		/// <summary> Terminates the execution of the process </summary>
		/// <remarks> The process does not terminate immediately 
		/// after the call </remarks>
		public void Terminate()
		{
			// Resume stoped tread
			if (this.IsPaused) {
				// We might get more callbacks so we should maintain consistent sate
				//AsyncContinue(); // Continue the process to get remaining callbacks
			}
			
			// Expose race condition - drain callback queue
			System.Threading.Thread.Sleep(0);
			
			// Stop&terminate - both must be called
			corProcess.Stop(uint.MaxValue);
			corProcess.Terminate(0);
			
			// Just give the command and continue without marking the process as
			// exited.  We will get ExitProcess callback soon
		}
		
		void SelectSomeThread()
		{
			if (this.SelectedThread != null && !this.SelectedThread.IsInValidState) {
				this.SelectedThread = null;
			}
			if (this.SelectedThread == null) {
				foreach(Thread thread in this.Threads) {
					if (thread.IsInValidState) {
						this.SelectedThread = thread;
						break;
					}
				}
			}
		}
		
		internal void SelectMostRecentStackFrameWithLoadedSymbols()
		{
			SelectSomeThread();
			if (this.SelectedThread != null) {
				this.SelectedThread.SelectedStackFrame = this.SelectedThread.MostRecentStackFrameWithLoadedSymbols;
			}
		}
		
		internal void DisableAllSteppers()
		{
			foreach(Thread thread in this.Threads) {
				foreach(Stepper stepper in thread.Steppers) {
					stepper.PauseWhenComplete = false;
				}
			}
		}
		
		/// <summary>
		/// Waits until the debugger pauses unless it is already paused.
		/// Use PausedReason to find out why it paused.
		/// </summary>
		public void WaitForPause()
		{
			while(this.IsRunning && !this.HasExpired) {
				debugger.MTA2STA.WaitForCall();
				debugger.MTA2STA.PerformAllCalls();
			}
			if (this.HasExpired) throw new ProcessExitedException();
		}
		
		public void WaitForPause(TimeSpan timeout)
		{
			DateTime endTime = Util.HighPrecisionTimer.Now + timeout;
			while(this.IsRunning && !this.HasExpired) {
				TimeSpan timeLeft = endTime - Util.HighPrecisionTimer.Now;
				if (timeLeft <= TimeSpan.FromMilliseconds(10)) break;
				//this.TraceMessage("Time left: " + timeLeft.TotalMilliseconds);
				debugger.MTA2STA.WaitForCall(timeLeft);
				debugger.MTA2STA.PerformCall();
			}
			if (this.HasExpired) throw new ProcessExitedException();
		}
		
		/// <summary>
		/// Waits until the precesses exits.
		/// </summary>
		public void WaitForExit()
		{
			while(!this.HasExpired) {
				debugger.MTA2STA.WaitForCall();
				debugger.MTA2STA.PerformAllCalls();
			}
		}
	}
}
