﻿// Copyright (C) NeoAxis Group Ltd. 8 Copthall, Roseau Valley, 00152 Commonwealth of Dominica.
using System;
using System.Text;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Threading;
using System.Drawing.Design;
using Xilium.CefGlue;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NeoAxis.UIWebBrowserControl;
using System.Windows.Forms;

namespace NeoAxis
{
	/// <summary>
	/// Web browser as a UI element.
	/// </summary>
	public partial class UIWebBrowser : UIControl
	{
		static bool isCefRuntimeInitialized;

		CefBrowser browser;
		CefBrowserHost browserHost;

		Vector2I viewSize;

		Component_Image texture;
		Vector2I textureSize;
		bool needUpdateTexture;
		bool needInvalidate = true;
		bool needRecreateTexture;

		string title;

		object renderBufferLock = new object();
		byte[] renderBuffer;
		Vector2I renderBufferForSize;

		volatile Cursor currentCursor;

		/////////////////////////////////////////

		/// <summary>
		/// The initial web page address.
		/// </summary>
		[DefaultValue( "" )]
		[Serialize]
		[DisplayName( "Start URL" )]
		public Reference<string> StartURL
		{
			get { if( _startURL.BeginGet() ) StartURL = _startURL.Get( this ); return _startURL.value; }
			set
			{
				if( _startURL.BeginSet( ref value ) )
				{
					try
					{
						StartURLChanged?.Invoke( this );
						OnChangeLoadProperties();
					}
					finally { _startURL.EndSet(); }
				}
			}
		}
		public event Action<UIWebBrowser> StartURLChanged;
		ReferenceField<string> _startURL = "";

		/// <summary>
		/// The initial file.
		/// </summary>
		//const string defaultStartFile = @"Base\Tools\UIWebBrowserDefaultPage.html";
		[DefaultValue( "" )]//defaultStartFile )]
		[Serialize]
		public Reference<string> StartFile
		{
			get { if( _startFile.BeginGet() ) StartFile = _startFile.Get( this ); return _startFile.value; }
			set
			{
				if( _startFile.BeginSet( ref value ) )
				{
					try
					{
						StartFileChanged?.Invoke( this );
						OnChangeLoadProperties();
					}
					finally { _startFile.EndSet(); }
				}
			}
		}
		public event Action<UIWebBrowser> StartFileChanged;
		ReferenceField<string> _startFile = "";// defaultStartFile;

		/// <summary>
		/// The initial content.
		/// </summary>
		const string startStringDefault = @"<h1 style='text-align: center;'><strong>Default page</strong></h1><p>&nbsp;</p><p style='text-align: center;'><span style='color: #ff6600;'>UIWebBrowser</span></p>";
		[DefaultValue( startStringDefault )]
		[Serialize]
		public Reference<string> StartString
		{
			get { if( _startString.BeginGet() ) StartString = _startString.Get( this ); return _startString.value; }
			set
			{
				if( _startString.BeginSet( ref value ) )
				{
					try
					{
						StartStringChanged?.Invoke( this );
						OnChangeLoadProperties();
					}
					finally { _startString.EndSet(); }
				}
			}
		}
		public event Action<UIWebBrowser> StartStringChanged;
		ReferenceField<string> _startString = startStringDefault;

		/// <summary>
		/// The zoom ratio of the browser.
		/// </summary>
		[DefaultValue( 0.0 )]
		[Serialize]
		[Range( -10.0f, 10.0f )]
		public Reference<double> Zoom
		{
			get { if( _zoom.BeginGet() ) Zoom = _zoom.Get( this ); return _zoom.value; }
			set
			{
				if( _zoom.BeginSet( ref value ) )
				{
					try
					{
						ZoomChanged?.Invoke( this );

						if( browserHost != null )
						{
							browserHost.SetZoomLevel( Zoom );
							needInvalidate = true;
						}
					}
					finally { _zoom.EndSet(); }
				}
			}
		}
		public event Action<UIWebBrowser> ZoomChanged;
		ReferenceField<double> _zoom = 0.0;

		//!!!!
		//int renderingIn3DHeightInPixels = 800;
		//bool renderingIn3DMipmaps;

		/////////////////////////////////////////

		void OnChangeLoadProperties()
		{
			if( !string.IsNullOrEmpty( StartURL ) )
				LoadURL( StartURL );
			else if( !string.IsNullOrEmpty( StartFile ) )
				LoadFile( StartFile );
			else if( !string.IsNullOrEmpty( StartString ) )
				LoadString( StartString );
			else
				LoadURL( "about:blank" );
		}

		///// <summary>
		///// The height of the browser in 3D space (in pixels).
		///// </summary>
		//[DefaultValue( 800 )]
		//[Serialize]
		//public int RenderingIn3DHeightInPixels
		//{
		//	get { return renderingIn3DHeightInPixels; }
		//	set
		//	{
		//		if( renderingIn3DHeightInPixels == value )
		//			return;
		//		if( value < 1 )
		//			value = 1;

		//		renderingIn3DHeightInPixels = value;
		//	}
		//}

		///// <summary>
		///// Use mipmaps in 3D space?
		///// </summary>
		//[DefaultValue( false )]
		//[Serialize]
		//public bool RenderingIn3DMipmaps
		//{
		//	get { return renderingIn3DMipmaps; }
		//	set
		//	{
		//		if( renderingIn3DMipmaps == value )
		//			return;
		//		renderingIn3DMipmaps = value;
		//		needRecreateTexture = true;
		//	}
		//}

		//!!!!
		/// <summary>
		/// Whether control can be focused or not.
		/// </summary>
		[Browsable( false )]
		public override bool CanFocus
		{
			get { return EnabledInHierarchy && VisibleInHierarchy; }
		}

		/// <summary>
		/// The CefBrowser used by the browser.
		/// </summary>
		[Browsable( false )]
		public CefBrowser Browser { get { return browser; } }

		protected override void OnEnabled()
		{
			RenderingSystem.RenderSystemEvent += RenderSystem_RenderSystemEvent;
		}

		protected override void OnDisabled()
		{
			RenderingSystem.RenderSystemEvent -= RenderSystem_RenderSystemEvent;

			DestroyBrowser();

			//never called
			//WebCore.Shutdown();

			if( texture != null )
			{
				texture.Dispose();
				texture = null;
			}
		}

		void RenderSystem_RenderSystemEvent( RenderSystemEvent name )
		{
			if( name == RenderSystemEvent.DeviceRestored )
				needUpdateTexture = true;
		}

		static void InitializeCefRuntime()
		{
			if( !IsSupportedByThisPlatform() )
				return;

			if( isCefRuntimeInitialized )
			{
				Log.Error( "UIWebBrowser: InitializeCefRuntime: The CefRuntime is already initialized." );
				return;
			}

			if( SystemSettings.CurrentPlatform == SystemSettings.Platform.Windows )
				NativeLibraryManager.PreLoadLibrary( Path.Combine( "CefGlue", "libcef" ) );

			//delete log file
			string realLogFileName = VirtualPathUtility.GetRealPathByVirtual( "user:Logs\\UIWebBrowser_CefGlue.log" );
			try
			{
				if( File.Exists( realLogFileName ) )
					File.Delete( realLogFileName );
			}
			catch { }

			try
			{
				CefRuntime.Load();
			}
			catch( DllNotFoundException ex )
			{
				Log.Error( "UIWebBrowser: InitializeCefRuntime: CefRuntime.Load: " + ex.Message );
				return;
			}
			catch( CefRuntimeException ex )
			{
				Log.Error( "UIWebBrowser: InitializeCefRuntime: CefRuntime.Load: " + ex.Message );
				return;
			}
			catch( Exception ex )
			{
				Log.Error( "UIWebBrowser: InitializeCefRuntime: CefRuntime.Load: " + ex.Message );
				return;
			}

			var mainArgs = new CefMainArgs( null );
			var cefApp = new SimpleApp();

			var exitCode = CefRuntime.ExecuteProcess( mainArgs, cefApp, IntPtr.Zero );
			if( exitCode != -1 )
			{
				Log.Error( "UIWebBrowser: InitializeCefRuntime: CefRuntime.ExecuteProcess: Exit code: {0}", exitCode );
				return;
			}

			var cefSettings = new CefSettings
			{
				SingleProcess = true,
				WindowlessRenderingEnabled = true,
				MultiThreadedMessageLoop = true,
				LogSeverity = CefLogSeverity.Verbose,
				LogFile = realLogFileName,
				BrowserSubprocessPath = "",
				CachePath = "",
			};

			///// <summary>
			///// Set to <c>true</c> to disable configuration of browser process features using
			///// standard CEF and Chromium command-line arguments. Configuration can still
			///// be specified using CEF data structures or via the
			///// CefApp::OnBeforeCommandLineProcessing() method.
			///// </summary>
			//public bool CommandLineArgsDisabled { get; set; }

			///// <summary>
			///// The fully qualified path for the resources directory. If this value is
			///// empty the cef.pak and/or devtools_resources.pak files must be located in
			///// the module directory on Windows/Linux or the app bundle Resources directory
			///// on Mac OS X. Also configurable using the "resources-dir-path" command-line
			///// switch.
			///// </summary>
			//public string ResourcesDirPath { get; set; }

			try
			{
				CefRuntime.Initialize( mainArgs, cefSettings, cefApp, IntPtr.Zero );
			}
			catch( CefRuntimeException ex )
			{
				Log.Error( "UIWebBrowser: InitializeCefRuntime: CefRuntime.Initialize: " + ex.Message );
				return;
			}

			isCefRuntimeInitialized = true;
		}

		static void ShutdownCefRuntime()
		{
			// shutdown CEF
			CefRuntime.Shutdown();
			isCefRuntimeInitialized = false;
		}

		void CreateBrowser()
		{
			if( !isCefRuntimeInitialized )
				InitializeCefRuntime();

			if( isCefRuntimeInitialized )
			{
				viewSize = GetNeededSize();

				var windowInfo = CefWindowInfo.Create();
				windowInfo.SetAsWindowless( IntPtr.Zero, false );

				var client = new WebClient( this );

				var settings = new CefBrowserSettings
				{
					// AuthorAndUserStylesDisabled = false,
				};

				//string r = GetURLFromVirtualFileName( "Maps\\Engine Features Demo\\Resources\\GUI\\FileTest.html" );
				//r = PathUtils.GetRealPathByVirtual( "Maps\\Engine Features Demo\\Resources\\GUI\\FileTest.html" );
				//CefBrowserHost.CreateBrowser( windowInfo, client, settings, r );//"about:blank" );
				//if( !string.IsNullOrEmpty( startUrl ) )
				//   LoadURL( startUrl );
				//LoadFileByVirtualFileName( "Maps\\Engine Features Demo\\Resources\\GUI\\FileTest.html" );

				string url = "about:blank";
				if( !string.IsNullOrEmpty( StartURL ) )
					url = StartURL;
				else if( !string.IsNullOrEmpty( StartFile ) )
					url = GetURLByFileName( StartFile );

				CefBrowserHost.CreateBrowser( windowInfo, client, settings, url );

				//CefBrowserHost.CreateBrowser( windowInfo, client, settings, "about:blank" );
				//if( !string.IsNullOrEmpty( startFile ) )
				//   LoadFileByVirtualFileName( startFile );
				//else if( !string.IsNullOrEmpty( startUrl ) )
				//   LoadURL( startUrl );

				//CefBrowserHost.CreateBrowser( windowInfo, client, settings, !string.IsNullOrEmpty( StartURL ) ? StartURL : "about:blank" );
				//if( !string.IsNullOrEmpty( startUrl ) )
				//   LoadURL( startUrl );
			}
		}

		void DestroyBrowser()
		{
			if( browser != null )
			{
				// TODO: What's the right way of disposing the browser instance?
				if( browserHost != null )
				{
					browserHost.CloseBrowser();
					browserHost.Dispose();
					browserHost = null;
				}

				if( browser != null )
				{
					browser.Dispose();
					browser = null;
				}
			}
		}

		public event Action<UIWebBrowser> BrowserCreated;

		internal void HandleAfterCreated( CefBrowser browser )
		{
			this.browser = browser;
			this.browserHost = browser.GetHost();

			needInvalidate = true;
			browserHost.SetZoomLevel( Zoom );

			BrowserCreated?.Invoke( this );

			if( string.IsNullOrEmpty( StartURL ) && string.IsNullOrEmpty( StartFile ) && !string.IsNullOrEmpty( StartString ) )
				LoadString( StartString );
		}

		public delegate void BeforePopupDelegate( UIWebBrowser sender, BeforePopupEventArgs args );
		public event BeforePopupDelegate BeforePopup;

		internal void OnBeforePopup( BeforePopupEventArgs e )
		{
			BeforePopup?.Invoke( this, e );

			if( !e.Handled )
			{
				LoadURL( e.TargetUrl );
				e.Handled = true;
			}
		}

		public delegate void TitleChangedDelegate( UIWebBrowser sender, string title );
		public event TitleChangedDelegate TitleChanged;

		internal void OnTitleChanged( string title )
		{
			this.title = title;
			TitleChanged?.Invoke( this, title );
		}

		public delegate void AddressChangedDelegate( UIWebBrowser sender, string address );
		public event AddressChangedDelegate AddressChanged;

		internal void OnAddressChanged( string address )
		{
			AddressChanged?.Invoke( this, address );
		}

		public delegate void TargetUrlChangedDelegate( UIWebBrowser sender, string targetUrl );
		public event TargetUrlChangedDelegate TargetUrlChanged;

		internal void OnTargetUrlChanged( string targetUrl )
		{
			TargetUrlChanged?.Invoke( this, targetUrl );
		}

		internal bool OnTooltip( string text )
		{
			//Console.WriteLine( "OnTooltip: {0}", text );
			return false;
		}

		public delegate void LoadingStateChangedDelegate( UIWebBrowser sender, bool loading, bool canGoBack, bool canGoForward );
		public event LoadingStateChangedDelegate LoadingStateChanged;

		internal void OnLoadingStateChange( bool loading, bool canGoBack, bool canGoForward )
		{
			LoadingStateChanged?.Invoke( this, loading, canGoBack, canGoForward );
		}

		public delegate void LoadStartDelegate( UIWebBrowser sender, CefFrame frame );
		public event LoadStartDelegate LoadStart;

		internal void OnLoadStart( CefFrame frame )
		{
			LoadStart?.Invoke( this, frame );
		}

		public delegate void LoadEndDelegate( UIWebBrowser sender, CefFrame frame, int httpStatusCode );
		public event LoadEndDelegate LoadEnd;

		internal void OnLoadEnd( CefFrame frame, int httpStatusCode )
		{
			LoadEnd?.Invoke( this, frame, httpStatusCode );
			needInvalidate = true;
		}

		public delegate void LoadErrorDelegate( UIWebBrowser sender, CefFrame frame, CefErrorCode errorCode, string errorText, string failedUrl );
		public event LoadErrorDelegate LoadError;

		internal void OnLoadError( CefFrame frame, CefErrorCode errorCode, string errorText, string failedUrl )
		{
			LoadError?.Invoke( this, frame, errorCode, errorText, failedUrl );
		}

		internal bool GetViewRect( ref CefRectangle rect )
		{
			bool rectProvided = false;
			CefRectangle browserRect = new CefRectangle();

			// TODO: simplify this
			//_mainUiDispatcher.Invoke(DispatcherPriority.Normal, new Action(delegate
			//{
			try
			{
				// The simulated screen and view rectangle are the same. This is necessary
				// for popup menus to be located and sized inside the view.
				browserRect.X = browserRect.Y = 0;
				browserRect.Width = ViewSize.X;
				browserRect.Height = ViewSize.Y;

				rectProvided = true;
			}
			catch( Exception ex )
			{
				Log.Error( "UIWebBrowser: Caught exception in GetViewRect: " + ex.Message );
				rectProvided = false;
			}
			//}));

			if( rectProvided )
				rect = browserRect;

			//_logger.Debug("GetViewRect result provided:{0} Rect: X{1} Y{2} H{3} W{4}", rectProvided, browserRect.X, browserRect.Y, browserRect.Height, browserRect.Width);

			return rectProvided;
		}

		internal void GetScreenPoint( int viewX, int viewY, ref int screenX, ref int screenY )
		{
			Point ptScreen = new Point();

			//_mainUiDispatcher.Invoke(DispatcherPriority.Normal, new Action(delegate
			//{
			try
			{
				//Point ptView = new Point(viewX, viewY);
				//ptScreen = PointToScreen(ptView);

				ptScreen.X = viewSize.X * viewX;
				ptScreen.Y = viewSize.Y * viewY;
			}
			catch( Exception ex )
			{
				Log.Error( "UIWebBrowser: Caught exception in GetScreenPoint: " + ex.Message );
			}
			//}));

			screenX = (int)ptScreen.X;
			screenY = (int)ptScreen.Y;
		}

		internal void HandlePaint( CefBrowser browser, CefPaintElementType type, CefRectangle[] dirtyRects, IntPtr buffer, int width, int height )
		{
			if( type == CefPaintElementType.View )
			{
				if( texture != null && width != 0 && height != 0 && width == viewSize.X && height == viewSize.Y )
				{
					//TO DO: dirtyRects

					try
					{
						int stride = width * 4;
						int sourceBufferSize = stride * height;

						lock( renderBufferLock )
						{
							Vector2I newSize = new Vector2I( width, height );
							if( renderBuffer == null || sourceBufferSize != renderBuffer.Length || renderBufferForSize != newSize )
							{
								renderBuffer = new byte[ sourceBufferSize ];
								renderBufferForSize = newSize;
							}
							Marshal.Copy( buffer, renderBuffer, 0, sourceBufferSize );
						}

						needUpdateTexture = true;
					}
					catch( Exception ex )
					{
						Log.Error( "UIWebBrowser: Caught exception in HandlePaint: " + ex.Message );
					}
				}
			}
			if( type == CefPaintElementType.Popup )
			{
				//TO DO?
			}
		}

		void UpdateTexture()
		{
			lock( renderBufferLock )
			{
				if( renderBuffer != null && renderBufferForSize == ViewSize && renderBuffer.Length == ViewSize.X * ViewSize.Y * 4 )
				{
					try
					{
						var gpuTexture = texture.Result;
						if( gpuTexture != null )
						{
							//!!!!sense to copy?
							//!!!!slowly?
							var data = (byte[])renderBuffer.Clone();

							var d = new GpuTexture.SurfaceData[] { new GpuTexture.SurfaceData( 0, 0, data ) };
							gpuTexture.SetData( d );
						}
					}
					catch( Exception ex )
					{
						Log.Error( "UIWebBrowser: Caught exception in UpdateTexture: " + ex.Message );
					}
				}
			}
		}

		Vector2I GetNeededSize()
		{
			Vector2I result;

			//!!!!3D
			//UIContainerScreen screenControlManager = ParentContainer as UIContainerScreen;
			//if( screenControlManager != null )
			{
				//screen gui

				Vector2I viewportSize = ParentContainer.Viewport.SizeInPixels;

				Vector2 size = viewportSize.ToVector2F() * GetScreenSize();
				//if( screenControlManager.CanvasRenderer._OutGeometryTransformEnabled )
				//	size *= screenControlManager.CanvasRenderer._OutGeometryTransformScale;

				result = size.ToVector2I();
				//result = new Vec2I( (int)( size.X + .9999f ), (int)( size.Y + .9999f ) );
			}
			//else
			//{
			//	//in-game gui

			//	//!!!!
			//	int height = 800;// renderingIn3DHeightInPixels;
			//	Vector2 screenSize = GetScreenSize();
			//	double width = (double)height * ( screenSize.X / screenSize.Y ) * ParentContainer.AspectRatio;
			//	result = new Vector2I( (int)( width + .9999f ), height );

			//	//int height = inGame3DGuiHeightInPixels;
			//	//if( height > RenderSystem.Instance.Capabilities.MaxTextureSize )
			//	//   height = RenderSystem.Instance.Capabilities.MaxTextureSize;

			//	//Vec2 screenSize = GetScreenSize();
			//	//double width = (double)height * ( screenSize.X / screenSize.Y ) * GetControlManager().AspectRatio;
			//	//result = new Vec2I( (int)( width + .9999f ), height );
			//}

			if( result.X < 1 )
				result.X = 1;
			if( result.Y < 1 )
				result.Y = 1;

			//fix max texture size
			if( result.X > RenderingSystem.Capabilities.MaxTextureSize || result.Y > RenderingSystem.Capabilities.MaxTextureSize )
			{
				double divideX = (double)result.X / (double)RenderingSystem.Capabilities.MaxTextureSize;
				double divideY = (double)result.Y / (double)RenderingSystem.Capabilities.MaxTextureSize;
				double divide = Math.Max( Math.Max( divideX, divideY ), 1 );
				if( divide != 1 )
				{
					result = ( result.ToVector2() / divide ).ToVector2I();
					if( result.X > RenderingSystem.Capabilities.MaxTextureSize )
						result.X = RenderingSystem.Capabilities.MaxTextureSize;
					if( result.Y > RenderingSystem.Capabilities.MaxTextureSize )
						result.Y = RenderingSystem.Capabilities.MaxTextureSize;
				}
			}

			return result;
		}

		protected virtual void OnResized( Vector2I oldSize, Vector2I newSize )
		{
			if( newSize.X > 0 && newSize.Y > 0 )
			{
				// If the window has already been created, just resize it
				browserHost?.WasResized();
			}
		}

		protected override void OnRenderUI( CanvasRenderer renderer )
		{
			base.OnRenderUI( renderer );

			Vector2I size = GetNeededSize();

			if( browser == null )
				CreateBrowser();

			//update brower engine and texture
			if( browser != null )
			{
				if( viewSize != size /*&& !browser.IsResizing */)
				{
					var oldSize = viewSize;
					viewSize = size;
					OnResized( oldSize, viewSize );
				}

				//create texture
				if( texture == null || textureSize != size || needRecreateTexture )
				{
					if( texture != null )
					{
						texture.Dispose();
						texture = null;
					}

					textureSize = size;

					bool mipmaps = false;
					//!!!!
					//if( ControlManager != null && ControlManager is UI3DControlContainer )
					//	mipmaps = renderingIn3DMipmaps;

					var usage = Component_Image.Usages.WriteOnly;
					if( mipmaps )
						usage |= Component_Image.Usages.AutoMipmaps;

					texture = ComponentUtility.CreateComponent<Component_Image>( null, true, false );
					texture.CreateType = Component_Image.TypeEnum._2D;
					texture.CreateSize = textureSize;
					texture.CreateMipmaps = mipmaps;// ? -1 : 0;
					texture.CreateFormat = PixelFormat.A8R8G8B8;
					texture.CreateUsage = usage;
					texture.Enabled = true;

					//Log.Info( textureSize.ToString() );

					//if( mipmaps )
					//{
					//	texture = TextureManager.Instance.Create( textureName, Texture.Type.Type2D, textureSize,
					//		1, -1, PixelFormat.A8R8G8B8, Texture.Usage.DynamicWriteOnlyDiscardable | Texture.Usage.AutoMipmap );
					//}
					//else
					//{
					//	texture = TextureManager.Instance.Create( textureName, Texture.Type.Type2D, textureSize,
					//		1, 0, PixelFormat.A8R8G8B8, Texture.Usage.DynamicWriteOnlyDiscardable );
					//}

					needUpdateTexture = true;
					needRecreateTexture = false;
				}

				if( needInvalidate )
				{
					browserHost.SetZoomLevel( Zoom );
					browserHost.Invalidate( new CefRectangle( 0, 0, 100000, 100000 ), CefPaintElementType.View );
					needInvalidate = false;
				}

				//update texture
				if( /*browser.IsDirty ||*/ needUpdateTexture )
				{
					if( texture != null )
						UpdateTexture();
					needUpdateTexture = false;
				}
			}

			//draw texture
			{
				//bool backColorZero = BackColor == new ColorValue( 0, 0, 0, 0 );

				//ColorValue color = new ColorValue( 1, 1, 1 );
				////ColorValue color = backColorZero ? new ColorValue( 1, 1, 1 ) : BackColor;
				//if( texture == null )
				//	color = new ColorValue( 0, 0, 0, color.Alpha );
				//color *= GetTotalColorMultiplier();

				//var color = GetTotalColorMultiplier();
				//if( color.Alpha > 0 )
				//{
				var color = new ColorValue( 1, 1, 1 );
				//color.Saturate();

				GetScreenRectangle( out var rect );

				Component_Image tex = null;
				if( renderBuffer != null && renderBufferForSize == ViewSize && renderBuffer.Length == ViewSize.X * ViewSize.Y * 4 )
					tex = texture;
				if( tex == null )
					tex = ResourceUtility.WhiteTexture2D;

				if( renderer.IsScreen )//&& !renderer._OutGeometryTransformEnabled )
				{
					////screen per pixel accuracy

					Vector2 viewportSize = renderer.ViewportForScreenCanvasRenderer.SizeInPixels.ToVector2F();
					var v = size.ToVector2() / viewportSize;
					Rectangle fixedRect = new Rectangle( rect.LeftTop, rect.LeftTop + v );

					//Vec2 leftTop = rect.LeftTop;
					//leftTop *= viewportSize;
					//leftTop = new Vec2( (int)( leftTop.X + .9999f ), (int)( leftTop.Y + .9999f ) );
					////!!!!!
					////if( RenderSystem.Instance.IsDirect3D() )
					////	leftTop -= new Vec2( .5f, .5f );
					//leftTop /= viewportSize;

					//Vec2 rightBottom = rect.RightBottom;
					//rightBottom *= viewportSize;
					//rightBottom = new Vec2( (int)( rightBottom.X + .9999f ), (int)( rightBottom.Y + .9999f ) );
					////!!!!!
					////if( RenderSystem.Instance.IsDirect3D() )
					////	rightBottom -= new Vec2( .5f, .5f );
					//rightBottom /= viewportSize;

					//Rect fixedRect = new Rect( leftTop, rightBottom );

					renderer.PushTextureFilteringMode( CanvasRenderer.TextureFilteringMode.Point );
					renderer.AddQuad( fixedRect, new Rectangle( 0, 0, 1, 1 ), tex, color, true );
					renderer.PopTextureFilteringMode();
				}
				else
					renderer.AddQuad( rect, new Rectangle( 0, 0, 1, 1 ), tex, color, true );
				//}
			}

			if( !IsSupportedByThisPlatform() )
			{
				var text = string.Format( "UIWebBrowser: {0} is not supported.", SystemSettings.CurrentPlatform );
				var center = GetScreenRectangle().GetCenter();
				renderer.AddText( text, center, EHorizontalAlignment.Center, EVerticalAlignment.Center, new ColorValue( 1, 0, 0 ) );
			}
		}

		static bool IsSupportedMouseButton( EMouseButtons button )
		{
			return button == EMouseButtons.Left || button == EMouseButtons.Middle || button == EMouseButtons.Right;
		}

		static CefMouseButtonType ToCefMouseButton( EMouseButtons button )
		{
			switch( button )
			{
			case EMouseButtons.Left: return CefMouseButtonType.Left;
			case EMouseButtons.Middle: return CefMouseButtonType.Middle;
			case EMouseButtons.Right: return CefMouseButtonType.Right;
			}
			return CefMouseButtonType.Left;
		}

		static CefEventFlags GetCurrentKeyboardModifiers()
		{
			CefEventFlags result = new CefEventFlags();

			//!!!!надо
			////Log.Fatal( "impl" );
			//if( EngineApp.Instance.IsKeyPressed( EKeys.Alt ) )
			//	result |= CefEventFlags.AltDown;
			//if( EngineApp.Instance.IsKeyPressed( EKeys.Shift ) )
			//	result |= CefEventFlags.ShiftDown;
			//if( EngineApp.Instance.IsKeyPressed( EKeys.Control ) )
			//	result |= CefEventFlags.ControlDown;
			//if( EngineApp.Instance.IsKeyPressed( EKeys.LWin ) ||
			//	EngineApp.Instance.IsKeyPressed( EKeys.RWin ) ||
			//	EngineApp.Instance.IsKeyPressed( EKeys.Command ) )
			//{
			//	result |= CefEventFlags.CommandDown;
			//}

			return result;
		}

		CefMouseEvent GetCurrentMouseEvent()
		{
			Vector2 pos = viewSize.ToVector2F() * MousePosition;
			var mouseEvent = new CefMouseEvent( (int)pos.X, (int)pos.Y, GetCurrentKeyboardModifiers() );
			return mouseEvent;
		}

		protected override bool OnMouseDown( EMouseButtons button )
		{
			if( EnabledInHierarchy && VisibleInHierarchy && browserHost != null && new Rectangle( 0, 0, 1, 1 ).Contains( MousePosition ) )
			{
				try
				{
					//!!!!
					Focus();

					if( IsSupportedMouseButton( button ) )
						browserHost.SendMouseClickEvent( GetCurrentMouseEvent(), ToCefMouseButton( button ), false, 1 );

					//_logger.Debug(string.Format("Browser_MouseDown: ({0},{1})", cursorPos.X, cursorPos.Y));
				}
				catch( Exception ex )
				{
					Log.Error( "UIWebBrowser: Caught exception in OnMouseDown: " + ex.Message );
				}

				return true;
			}
			else
			{
				//!!!!
				Unfocus();
			}

			return base.OnMouseDown( button );
		}

		protected override bool OnMouseUp( EMouseButtons button )
		{
			//bool result = base.OnMouseUp( button );

			if( EnabledInHierarchy && VisibleInHierarchy && browserHost != null && IsSupportedMouseButton( button ) )
			{
				try
				{
					//Focus();

					if( IsSupportedMouseButton( button ) )
						browserHost.SendMouseClickEvent( GetCurrentMouseEvent(), ToCefMouseButton( button ), true, 1 );

					//_logger.Debug(string.Format("Browser_MouseDown: ({0},{1})", cursorPos.X, cursorPos.Y));
				}
				catch( Exception ex )
				{
					Log.Error( "UIWebBrowser: Caught exception in OnMouseUp: " + ex.Message );
				}

				return true;
			}

			return false;
			//return result;
		}

		protected override bool OnMouseDoubleClick( EMouseButtons button )
		{
			if( EnabledInHierarchy && VisibleInHierarchy && browserHost != null && new Rectangle( 0, 0, 1, 1 ).Contains( MousePosition ) )
			{
				try
				{
					Focus();

					if( IsSupportedMouseButton( button ) )
						browserHost.SendMouseClickEvent( GetCurrentMouseEvent(), ToCefMouseButton( button ), false, 2 );

					//_logger.Debug(string.Format("Browser_MouseDown: ({0},{1})", cursorPos.X, cursorPos.Y));
				}
				catch( Exception ex )
				{
					Log.Error( "UIWebBrowser: Caught exception in OnMouseDoubleClick: " + ex.Message );
				}

				return true;
			}

			return base.OnMouseDoubleClick( button );
		}

		protected override bool OnMouseWheel( int delta )
		{
			bool result = base.OnMouseWheel( delta );

			if( EnabledInHierarchy && VisibleInHierarchy && browserHost != null && new Rectangle( 0, 0, 1, 1 ).Contains( MousePosition ) )
			{
				try
				{
					browserHost.SendMouseWheelEvent( GetCurrentMouseEvent(), 0, delta );
				}
				catch( Exception ex )
				{
					Log.Error( "UIWebBrowser: Caught exception in OnMouseWheel: " + ex.Message );
				}
			}

			return result;
		}

		protected override void OnMouseMove( Vector2 mouse )
		{
			base.OnMouseMove( mouse );

			if( EnabledInHierarchy && VisibleInHierarchy && browserHost != null )
			{
				try
				{
					browserHost.SendMouseMoveEvent( GetCurrentMouseEvent(), false );
					//_logger.Debug(string.Format("Browser_MouseMove: ({0},{1})", cursorPos.X, cursorPos.Y));
				}
				catch( Exception ex )
				{
					Log.Error( "UIWebBrowser: Caught exception in OnMouseMove: " + ex.Message );
				}
			}
		}

		protected override bool OnKeyDown( KeyEvent e )
		{
			if( Focused && EnabledInHierarchy && VisibleInHierarchy && browserHost != null )
			{
				browserHost.SendFocusEvent( true );

				try
				{
					//_logger.Debug(string.Format("KeyDown: system key {0}, key {1}", arg.SystemKey, arg.Key));
					CefKeyEvent keyEvent = new CefKeyEvent()
					{
						EventType = CefKeyEventType.RawKeyDown,
						WindowsKeyCode = (int)e.Key /*KeyInterop.VirtualKeyFromKey(arg.Key == Key.System ? arg.SystemKey : arg.Key)*/,
						NativeKeyCode = (int)e.Key,/*0*/
												   /*IsSystemKey = e.Key == EKeys.System*/
					};

					keyEvent.Modifiers = GetCurrentKeyboardModifiers();

					browserHost.SendKeyEvent( keyEvent );
				}
				catch( Exception ex )
				{
					Log.Error( "UIWebBrowser: Caught exception in OnKeyDown: " + ex.Message );
				}

				//arg.Handled = HandledKeys.Contains(arg.Key);

				return true;
			}

			return base.OnKeyDown( e );
		}

		protected override bool OnKeyPress( KeyPressEvent e )
		{
			if( Focused && EnabledInHierarchy && VisibleInHierarchy && browserHost != null )
			{
				browserHost.SendFocusEvent( true );

				try
				{
					//_logger.Debug(string.Format("KeyDown: system key {0}, key {1}", arg.SystemKey, arg.Key));
					CefKeyEvent keyEvent = new CefKeyEvent()
					{
						EventType = CefKeyEventType.Char,
						WindowsKeyCode = (int)e.KeyChar
					};

					keyEvent.Modifiers = GetCurrentKeyboardModifiers();

					browserHost.SendKeyEvent( keyEvent );
				}
				catch( Exception ex )
				{
					Log.Error( "UIWebBrowser: Caught exception in OnKeyDown: " + ex.Message );
				}

				//arg.Handled = true;

				return true;
			}

			return base.OnKeyPress( e );
		}

		protected override bool OnKeyUp( KeyEvent e )
		{
			if( EnabledInHierarchy && VisibleInHierarchy && browserHost != null )
			{
				browserHost.SendFocusEvent( true );

				try
				{
					//_logger.Debug(string.Format("KeyDown: system key {0}, key {1}", arg.SystemKey, arg.Key));
					CefKeyEvent keyEvent = new CefKeyEvent()
					{
						EventType = CefKeyEventType.KeyUp,
						WindowsKeyCode = (int)e.Key /*KeyInterop.VirtualKeyFromKey(arg.Key == Key.System ? arg.SystemKey : arg.Key)*/,
						NativeKeyCode = (int)e.Key,/*0*/
												   /*IsSystemKey = e.Key == EKeys.System*/
					};

					keyEvent.Modifiers = GetCurrentKeyboardModifiers();

					browserHost.SendKeyEvent( keyEvent );
				}
				catch( Exception ex )
				{
					Log.Error( "UIWebBrowser: Caught exception in OnKeyDown: " + ex.Message );
				}

				//arg.Handled = true;
			}

			return base.OnKeyUp( e );
		}

		public void LoadURL( string url )
		{
			// Remove leading whitespace from the URL
			string url2 = url.TrimStart();

			if( string.IsNullOrEmpty( url2 ) )
				url2 = "about:blank";

			browser?.GetMainFrame().LoadUrl( url2 );
		}

		static string GetURLByFileName( string virtualOrRealFileName )
		{
			string realFileName = virtualOrRealFileName;
			try
			{
				if( !Path.IsPathRooted( virtualOrRealFileName ) )
					realFileName = VirtualPathUtility.GetRealPathByVirtual( virtualOrRealFileName );
			}
			catch { }

			var url = "file:///" + realFileName;
			return url;
		}

		public void LoadFile( string virtualOrRealFileName )
		{
			LoadURL( GetURLByFileName( virtualOrRealFileName ) );
		}

		//public void LoadFileByVirtualFileName( string virtualFileName )
		//{
		//	if( browser == null )
		//		return;

		//	//!!!!!
		//	//if( VirtualFile.InsidePackage( virtualFileName ) )
		//	//{
		//	//	Log.Warning( "UIWebBrowser: LoadFileByVirtualFileName: Loading from archive is not supported." );
		//	//	return;
		//	//}

		//	string url = GetURLFromVirtualFileName( virtualFileName );
		//	LoadURL( url );
		//}

		//public void LoadFileByRealFileName( string realFileName )
		//{
		//	if( browser == null )
		//		return;
		//	string url = "file:///" + realFileName;
		//	LoadURL( url );
		//}

		public void LoadString( string content, string url = "about:blank" )
		{
			// Remove leading whitespace from the URL
			string url2 = url.TrimStart();

			if( string.IsNullOrEmpty( url2 ) )
				url2 = "about:blank";

			browser?.GetMainFrame().LoadString( content, url2 );
		}

		//public void LoadHTML( string html, string frameName )
		//{
		//    if( browser != null )
		//        browser.GetMainFrame().LoadRequest(html, frameName);
		//}

		//public void LoadHTML( string html )
		//{
		//    LoadHTML( html, "" );
		//}

		public void ExecuteJavaScript( string code, string url, int line )
		{
			browser?.GetMainFrame().ExecuteJavaScript( code, url, line );
		}

		[Browsable( false )]
		public bool CanGoBack
		{
			get
			{
				if( browser != null )
					return browser.CanGoBack;
				else
					return false;
			}
		}

		public void GoBack()
		{
			browser?.GoBack();
		}

		[Browsable( false )]
		public bool CanGoForward
		{
			get
			{
				if( browser != null )
					return browser.CanGoForward;
				else
					return false;
			}
		}

		public void GoForward()
		{
			browser?.GoForward();
		}

		public void Stop()
		{
			browser?.StopLoad();
		}

		public void Reload()
		{
			browser?.Reload();
		}

		//[Browsable( false )]
		//public string Source
		//{
		//   get
		//   {
		//      if( browser != null )
		//      {
		//         //return browser.GetMainFrame().GetSource();
		//      }
		//      return "";
		//   }
		//}

		[Browsable( false )]
		public string Title
		{
			get { return title; }
		}

		[Browsable( false )]
		public string TargetURL
		{
			get
			{
				if( browser != null )
					return browser.GetMainFrame().Url;
				return "";
			}
		}

		public static bool IsSupportedByThisPlatform()
		{
			//!!!!!
			if( SystemSettings.CurrentPlatform == SystemSettings.Platform.MacOS )
				return false;
			return true;
		}

		[Browsable( false )]
		public Vector2I ViewSize
		{
			get { return viewSize; }
		}

		internal void HandleCursorChange( IntPtr cursorHandle )
		{
			try
			{
				currentCursor = new Cursor( cursorHandle );
			}
			catch { }
		}

		[Browsable( false )]
		public Cursor CurrentCursor
		{
			get { return currentCursor; }
		}

		public delegate void DownloadBeforeDelegate( UIWebBrowser sender, CefDownloadItem downloadItem, string suggestedName, CefBeforeDownloadCallback callback );
		public event DownloadBeforeDelegate DownloadBefore;

		internal void PerformDownloadBefore( CefDownloadItem downloadItem, string suggestedName, CefBeforeDownloadCallback callback )
		{
			DownloadBefore?.Invoke( this, downloadItem, suggestedName, callback );
		}

		public delegate void DownloadUpdatedDelegate( UIWebBrowser sender, CefDownloadItem downloadItem, CefDownloadItemCallback callback );
		public event DownloadUpdatedDelegate DownloadUpdated;

		internal void PerformDownloadUpdated( CefDownloadItem downloadItem, CefDownloadItemCallback callback )
		{
			DownloadUpdated?.Invoke( this, downloadItem, callback );
		}

	}
}
