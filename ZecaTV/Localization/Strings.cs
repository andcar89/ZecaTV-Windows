namespace ZecaTV.Localization
{
    /// <summary>
    /// Every user-facing string in the app, in English and Portuguese, picked by <see cref="Language"/>.
    /// XAML reads these via <c>{x:Static loc:Strings.SomeProperty}</c>, which resolves once when that
    /// XAML is loaded — so changing <see cref="Language"/> takes effect immediately in any window
    /// opened afterward, but an already-open window (MainWindow included) needs to be reopened, or the
    /// app restarted, to fully re-render in the new language. Code-behind reads the same properties
    /// directly for strings it builds itself (status messages, error text, generated menu items).
    /// </summary>
    public static class Strings
    {
        /// <summary>Set once at startup from SettingsStore.Current.Language, and again immediately when
        /// the user changes it in a language selector — see SettingsWindow/SourceWindow.</summary>
        public static AppLanguage Language { get; set; } = AppLanguage.English;

        private static bool Pt => Language == AppLanguage.Portuguese;

        // ---- Shared / built-in category names --------------------------------------------------
        public static string CategoryFavorites => Pt ? "Favoritos" : "Favorites";
        public static string CategoryAllChannels => Pt ? "Todos os canais" : "All channels";
        public static string CategoryUncategorized => Pt ? "Sem categoria" : "Uncategorized";

        // ---- MainWindow: top bar ------------------------------------------------------------------
        public static string SearchPlaceholder => Pt ? "Pesquisar todos os canais…" : "Search all channels…";
        public static string ReloadButton => Pt ? "Recarregar" : "Reload";
        public static string ReloadButtonTooltip => Pt ? "Descarregar novamente a lista (F5)" : "Download the playlist again (F5)";
        public static string TvGuideButton => Pt ? "Guia TV" : "TV Guide";
        public static string TvGuideButtonTooltip => Pt ? "Guia TV (G)" : "TV Guide (G)";
        public static string SettingsButton => Pt ? "Definições…" : "Settings…";
        public static string SwitchPlaylistTooltip => Pt ? "Mudar de lista" : "Switch playlist";
        public static string CategoriesHeader => Pt ? "CATEGORIAS" : "CATEGORIES";
        public static string FavoriteTooltip => Pt ? "Favorito" : "Favorite";
        public static string CatchupAvailableTooltip => Pt ? "Catch-up disponível" : "Catch-up available";

        // ---- MainWindow: Live TV / Movies / TV Shows section tabs -----------------------------
        public static string SectionLive => Pt ? "TV Direto" : "Live TV";
        public static string SectionMovies => Pt ? "Filmes" : "Movies";
        public static string SectionShows => Pt ? "Séries" : "TV Shows";
        public static string BackToShowsTooltip => Pt ? "Voltar às séries" : "Back to shows";
        public static string LoadingEpisodes => Pt ? "A carregar episódios…" : "Loading episodes…";
        public static string CouldntLoadEpisodes => Pt
            ? "Não foi possível carregar os episódios desta série."
            : "Couldn't load this show's episodes.";

        // ---- MainWindow: category right-click — manual Live/Movies/Shows correction (M3U only) -----
        public static string TreatCategoryAsMenuItem => Pt ? "Tratar categoria como" : "Treat category as";
        public static string CategoryAutomaticMenuItem => Pt ? "Automático (tipo detetado)" : "Automatic (detected type)";

        // ---- MainWindow: welcome / error panel -------------------------------------------------
        public static string WelcomeTitle => Pt ? "Bem-vindo ao ZecaTV" : "Welcome to ZecaTV";
        public static string WelcomeMessage => Pt
            ? "Adicione uma lista M3U ou uma conta Xtream Codes para começar."
            : "Add an M3U playlist or Xtream Codes login to get started.";
        public static string TryAgainButton => Pt ? "Tentar novamente" : "Try again";
        public static string AddPlaylistButton => Pt ? "Adicionar lista" : "Add playlist";
        public static string EditPlaylistButton => Pt ? "Editar lista" : "Edit playlist";
        public static string CouldntLoadPlaylistTitle => Pt ? "Não foi possível carregar a lista" : "Couldn't load the playlist";
        public static string PlaylistEmptyMessage => Pt
            ? "A lista foi carregada, mas não contém canais."
            : "The playlist loaded, but it doesn't contain any channels.";
        public static string LoadingPlaylist => Pt ? "A carregar a lista…" : "Loading playlist…";
        public static string ReloadFailedPrefix => Pt ? "Falha ao recarregar: " : "Reload failed: ";
        public static string ChannelsCategoriesFormat => Pt ? "{0:N0} canais · {1:N0} categorias" : "{0:N0} channels · {1:N0} categories";

        // ---- MainWindow: player / idle / status ------------------------------------------------
        public static string SelectChannelHint => Pt ? "Escolha um canal para começar a ver" : "Select a channel to start watching";
        public static string Connecting => Pt ? "A ligar…" : "Connecting…";
        public static string Reconnecting => Pt ? "A restabelecer ligação…" : "Reconnecting…";
        public static string RetryHintFullscreen => Pt ? "Prima Enter para tentar novamente." : "Press Enter to try again.";
        public static string RetryHintWindowed => Pt ? "Clique novamente no canal para tentar de novo." : "Click the channel again to retry.";
        public static string SearchResultsFormat => Pt ? "RESULTADOS PARA \u201C{0}\u201D  \u00B7  {1:N0}" : "SEARCH RESULTS FOR \u201C{0}\u201D  \u00B7  {1:N0}";

        // ---- MainWindow: on-screen messages / channel menu -------------------------------------
        public static string AddedToFavorites => Pt ? "★  Adicionado aos favoritos" : "★  Added to favorites";
        public static string RemovedFromFavorites => Pt ? "Removido dos favoritos" : "Removed from favorites";
        public static string StreamUrlCopied => Pt ? "URL do canal copiado" : "Stream URL copied";
        public static string CouldntCopy => Pt ? "Não foi possível copiar — área de transferência ocupada, tente de novo" : "Couldn't copy — clipboard busy, try again";
        public static string Muted => Pt ? "Sem som" : "Muted";
        public static string VolumeFormat => Pt ? "Volume  {0}%" : "Volume  {0}%";
        public static string WatchFullscreenMenuItem => Pt ? "Ver em ecrã inteiro" : "Watch fullscreen";
        public static string PlayMenuItem => Pt ? "Reproduzir" : "Play";
        public static string AddToFavoritesMenuItem => Pt ? "★  Adicionar aos favoritos" : "★  Add to favorites";
        public static string RemoveFromFavoritesMenuItem => Pt ? "☆  Remover dos favoritos" : "☆  Remove from favorites";
        public static string AssignChannelEpgMenuItem => Pt ? "Atribuir EPG ao canal…" : "Assign channel EPG…";
        public static string CopyStreamUrlMenuItem => Pt ? "Copiar URL do canal" : "Copy stream URL";

        // ---- MainWindow: video right-click menu (audio/subtitle tracks) ------------------------
        public static string AudioTrackMenuItem => Pt ? "Faixa de áudio" : "Audio track";
        public static string SubtitlesMenuItem => Pt ? "Legendas" : "Subtitles";
        public static string SubtitlesOffMenuItem => Pt ? "Desligadas" : "Off";
        public static string NoTracksAvailable => Pt ? "(nenhuma disponível)" : "(none available)";

        // ---- MainWindow: video right-click menu (aspect ratio) ---------------------------------
        public static string VideoRatioMenuItem => Pt ? "Proporção do vídeo" : "Video ratio";
        public static string VideoRatioFit => Pt ? "Ajustar (padrão)" : "Fit (default)";
        public static string VideoRatioFill => Pt ? "Esticar para preencher" : "Stretch to fill";
        public static string VideoRatio4x3 => "4:3";
        public static string VideoRatio16x9 => "16:9";

        // ---- MainWindow: fullscreen side panel hint --------------------------------------------
        public static string SidePanelHint => Pt
            ? "↑ ↓ escolher · Enter reproduzir · ← categorias · → / Esc fechar · Ctrl+D favorito"
            : "↑ ↓ choose · Enter play · ← categories · → / Esc close · Ctrl+D favorite";

        // ---- PlayerService: playback error messages --------------------------------------------
        public static string ErrorCouldntPlayChannel => Pt ? "Não foi possível reproduzir este canal." : "Couldn't play this channel.";
        public static string ErrorStreamEnded => Pt ? "O canal terminou." : "The stream ended.";
        public static string ErrorChannelNoResponse => Pt ? "O canal não respondeu." : "The channel didn't respond.";
        public static string ErrorStreamStopped => Pt ? "O canal parou." : "The stream stopped.";
        public static string ErrorNeedsLogin => Pt ? "O canal pediu um nome de utilizador e palavra-passe." : "The stream asked for a username and password.";

        // ---- ErrorText: network/load error messages --------------------------------------------
        public static string ErrorInvalidUrl => Pt ? "Esse endereço não é um URL válido." : "That address isn't a valid URL.";
        public static string ErrorServerTimeout => Pt ? "O servidor não respondeu a tempo." : "The server didn't respond in time.";
        public static string ErrorCouldntReachServerFormat => Pt ? "Não foi possível contactar o servidor ({0})." : "Couldn't reach the server ({0}).";
        public static string ErrorAccessRefusedFormat => Pt ? "{0}: acesso recusado pelo servidor. Verifique o seu login ou subscrição." : "{0}: the server refused access. Check your login or subscription.";

        // ---- SourceWindow (Playlist / Add playlist) --------------------------------------------
        public static string PlaylistDialogTitle => Pt ? "Lista de reprodução" : "Playlist";
        public static string PlaylistDialogHeading => Pt ? "De onde vêm os seus canais?" : "Where do your channels come from?";
        public static string PlaylistNameLabel => Pt ? "Nome da lista" : "Playlist name";
        public static string M3uUrlOption => Pt ? "URL M3U" : "M3U URL";
        public static string M3uFileOption => Pt ? "Ficheiro M3U" : "M3U file";
        public static string XtreamOption => Pt ? "Xtream Codes" : "Xtream Codes";
        public static string PlaylistUrlLabel => Pt ? "URL da lista" : "Playlist URL";
        public static string EpgUrlLabel => Pt
            ? "URL do EPG (opcional — usa-se o guia da própria lista se ficar em branco; pode indicar vários URLs separados por vírgulas)"
            : "EPG URL (optional — the playlist's own guide is used if left empty; you can enter several URLs separated by commas)";
        public static string PlaylistFileLabel => Pt ? "Ficheiro da lista" : "Playlist file";
        public static string BrowseButton => Pt ? "Procurar…" : "Browse…";
        public static string ServerLabel => Pt ? "Servidor (ex.: http://exemplo.com:8080)" : "Server (e.g. http://example.com:8080)";
        public static string UsernameLabel => Pt ? "Nome de utilizador" : "Username";
        public static string PasswordLabel => Pt ? "Palavra-passe" : "Password";
        public static string ShowButton => Pt ? "Mostrar" : "Show";
        public static string HideButton => Pt ? "Ocultar" : "Hide";
        public static string CancelButton => Pt ? "Cancelar" : "Cancel";
        public static string SaveAndLoadButton => Pt ? "Guardar e carregar" : "Save and load";
        public static string ChooseM3uFileDialogTitle => Pt ? "Escolha uma lista M3U" : "Choose an M3U playlist";
        public static string EnterPlaylistUrl => Pt ? "Introduza o URL da lista." : "Enter the playlist URL.";
        public static string ChoosePlaylistFileFirst => Pt ? "Escolha primeiro um ficheiro de lista." : "Choose a playlist file first.";
        public static string FillServerUserPassword => Pt ? "Preencha o servidor, o nome de utilizador e a palavra-passe." : "Fill in the server, username and password.";
        public static string CouldntLoadPlaylistPrefix => Pt ? "Não foi possível carregar a lista: " : "Couldn't load the playlist: ";
        public static string LanguageEnglish => "English";
        public static string LanguagePortuguese => "Português";
        public static string LanguageEnglishShort => "EN";
        public static string LanguagePortugueseShort => "PT";

        // ---- SettingsWindow ---------------------------------------------------------------------
        public static string SettingsTitle => Pt ? "Definições" : "Settings";
        public static string LanguageSection => Pt ? "IDIOMA" : "LANGUAGE";
        public static string LanguageChangedNote => Pt
            ? "Novas janelas já aparecem no novo idioma. Reinicie o ZecaTV para atualizar esta janela e a principal."
            : "New windows already show the new language. Restart ZecaTV to update this window and the main one.";
        public static string PlaylistsSection => Pt ? "LISTAS DE REPRODUÇÃO" : "PLAYLISTS";
        public static string SwitchButton => Pt ? "Mudar" : "Switch";
        public static string EditButton => Pt ? "Editar…" : "Edit…";
        public static string RemoveButton => Pt ? "Remover" : "Remove";
        public static string ActiveBadge => Pt ? "ATIVA" : "ACTIVE";
        public static string AddPlaylistEllipsisButton => Pt ? "Adicionar lista…" : "Add playlist…";
        public static string EpgSection => "EPG";
        public static string ManageSourcesButton => Pt ? "Gerir fontes…" : "Manage sources…";
        public static string ReloadAllGuideDataButton => Pt ? "Recarregar todo o guia" : "Reload all guide data";
        public static string AssignChannelEpgButton => Pt ? "Atribuir EPG ao canal…" : "Assign channel EPG…";
        public static string PlaybackSection => Pt ? "REPRODUÇÃO" : "PLAYBACK";
        public static string ResumeLastChannelCheckbox => Pt ? "Retomar o último canal ao iniciar o ZecaTV" : "Resume the last channel when ZecaTV starts";
        public static string BackgroundRefreshCheckbox => Pt
            ? "Atualizar automaticamente a lista e o guia EPG em segundo plano"
            : "Automatically refresh the playlist and EPG guide in the background";
        public static string BackgroundRefreshIntervalLabel => Pt ? "A cada" : "Every";
        public static string MinimizeToTrayCheckbox => Pt
            ? "Minimizar para a área de notificação (junto ao relógio) em vez da barra de tarefas"
            : "Minimize to the system tray (near the clock) instead of the taskbar";
        public static string TrayTooltip => "ZecaTV";
        public static string TrayOpenMenuItem => Pt ? "Abrir o ZecaTV" : "Open ZecaTV";
        public static string TrayExitMenuItem => Pt ? "Sair" : "Exit";
        public static string WindowSection => Pt ? "JANELA" : "WINDOW";
        public static string ChannelEntryFormat => Pt ? "Canal: {0}" : "Channel: {0}";
        public static string ChannelNotFoundFormat => Pt ? "Canal {0} não encontrado" : "Channel {0} not found";
        public static string ControlsSection => Pt ? "CONTROLOS" : "CONTROLS";
        public static string ControlsSectionBody => Pt
            ? "Atalhos de teclado e rato para reprodução, navegação e o Guia TV."
            : "Keyboard and mouse shortcuts for playback, browsing, and the TV Guide.";
        public static string ViewControlsButton => Pt ? "Ver controlos…" : "View controls…";
        public static string M3uEditorSection => Pt ? "EDITOR M3U" : "M3U EDITOR";
        public static string M3uEditorSectionBody => Pt
            ? "Uma ferramenta web independente para edição e gestão avançada de listas M3U — abre no seu navegador."
            : "A standalone web tool for advanced M3U playlist editing and management — opens in your browser.";
        public static string OpenM3uEditorButton => Pt
            ? "Editor/Gestor Avançado de Listas M3U"
            : "M3U Playlist Advanced Editor/Manager";
        public static string AboutSection => Pt ? "SOBRE" : "ABOUT";
        public static string OpenLogsFolderButton => Pt ? "Abrir pasta de registos" : "Open logs folder";
        public static string CloseButton => Pt ? "Fechar" : "Close";
        public static string NoEpgSourcesYet => Pt
            ? "Ainda não há fontes de EPG — adicione uma, ou surgirá automaticamente quando o guia da sua lista carregar."
            : "No EPG sources yet — add one, or one will appear automatically once your playlist's guide loads.";
        public static string EpgSourcesNoneLoadedFormat => Pt ? "{0:N0} fonte(s) configurada(s), nenhuma carregada ainda." : "{0:N0} source(s) configured, none loaded yet.";
        public static string EpgSourcesLoadedFormat => Pt
            ? "{0:N0} de {1:N0} fonte(s) carregada(s) — {2:N0} canais de guia, {3:N0} programas."
            : "{0:N0} of {1:N0} source(s) loaded — {2:N0} guide channels, {3:N0} programmes.";
        public static string XtreamSourceSummaryFormat => Pt ? "Xtream — {0} ({1})" : "Xtream — {0} ({1})";

        // ---- SettingsWindow: category overrides (manual Live/Movies/Shows corrections, M3U only) -----
        public static string CategoryOverridesSection => Pt ? "CORREÇÕES DE CATEGORIA" : "CATEGORY OVERRIDES";
        public static string CategoryOverridesBody => Pt
            ? "Categorias que corrigiu manualmente quando o ZecaTV adivinhou mal o tipo de conteúdo — clique com o botão direito numa categoria na barra lateral e escolha \"Tratar categoria como\"."
            : "Categories you've manually corrected after ZecaTV guessed their content type wrong — right-click a category in the sidebar and choose \"Treat category as…\".";
        public static string NoCategoryOverridesYet => Pt ? "Ainda não há correções." : "No corrections yet.";

        // ---- ControlsWindow -----------------------------------------------------------------------
        public static string ControlsTitle => Pt ? "Controlos" : "Controls";
        public static string GroupPlayback => Pt ? "REPRODUÇÃO" : "PLAYBACK";
        public static string GroupBrowsing => Pt ? "NAVEGAÇÃO" : "BROWSING";
        public static string ActionPreviewChannel => Pt ? "Pré-visualizar um canal" : "Preview a channel";
        public static string KeysClickOrEnter => Pt ? "Clique, ou Enter" : "Click, or Enter";
        public static string ActionFullscreen => Pt ? "Ecrã inteiro" : "Fullscreen";
        public static string KeysFullscreen => Pt ? "Clique / Enter de novo, F11, F, duplo clique no vídeo" : "Click / Enter again, F11, F, double-click video";
        public static string ActionExitFullscreen => Pt ? "Sair do ecrã inteiro" : "Exit fullscreen";
        public static string KeysExitFullscreen => Pt ? "Esc, F11, F, duplo clique no vídeo" : "Esc, F11, F, double-click video";
        public static string ActionChangeChannelFullscreen => Pt ? "Mudar de canal (ecrã inteiro)" : "Change channel (fullscreen)";
        public static string KeysArrowsPgUpDn => "↑ / ↓ or PgUp / PgDn";
        public static string ActionPauseResume => Pt ? "Pausar / retomar (Filmes e Séries)" : "Pause / resume (Movies & TV Shows)";
        public static string KeysPauseResume => Pt ? "Espaço (ecrã inteiro), ou o botão de pausa" : "Space (fullscreen), or the pause button";
        public static string ActionSeek => Pt ? "Avançar / recuar (Filmes e Séries)" : "Seek (Movies & TV Shows)";
        public static string KeysSeek => Pt ? "← / → (ecrã inteiro), ou arraste a barra" : "← / → (fullscreen), or drag the seek bar";
        public static string ActionVolume => Pt ? "Volume" : "Volume";
        public static string KeysVolume => Pt ? "Roda do rato sobre o vídeo, + / −" : "Mouse wheel over video, + / −";
        public static string ActionMute => Pt ? "Sem som" : "Mute";
        public static string ActionAudioSubtitles => Pt ? "Proporção, áudio, legendas" : "Video ratio, audio, subtitles";
        public static string KeysAudioSubtitles => Pt ? "Clique direito no vídeo (a reproduzir)" : "Right-click the video (while playing)";
        public static string ActionDirectChannel => Pt ? "Ir diretamente para um canal" : "Jump straight to a channel";
        public static string KeysDirectChannel => Pt
            ? "Escreva o número do canal, Enter para confirmar"
            : "Type the channel's number, Enter to confirm";
        public static string ActionReloadPlaylist => Pt ? "Recarregar lista" : "Reload playlist";
        public static string ActionSearchAllChannels => Pt ? "Pesquisar todos os canais" : "Search all channels";
        public static string KeysSearch => Pt ? "Ctrl+F, ou clique na caixa de pesquisa" : "Ctrl+F, or click the search box";
        public static string ActionBrowseMoviesShows => Pt ? "Procurar Filmes e Séries" : "Browse Movies & TV Shows";
        public static string KeysBrowseMoviesShows => Pt ? "Separadores na barra lateral" : "Tabs in the sidebar";
        public static string ActionMoveCategoriesChannels => Pt ? "Mover entre categorias e canais" : "Move between categories and channels";
        public static string ActionChannelListFullscreen => Pt ? "Lista de canais (ecrã inteiro)" : "Channel list (fullscreen)";
        public static string KeysChannelListFullscreen => Pt
            ? "← (de novo para categorias), → ou Esc para fechar"
            : "← (again for categories), → or Esc to close";
        public static string ActionBackFromEpisodes => Pt ? "Voltar à grelha de séries" : "Back to the shows grid";
        public static string KeysBackFromEpisodes => Pt ? "Esc, ou o botão de voltar" : "Esc, or the back button";
        public static string ActionAddRemoveFavorite => Pt ? "Adicionar / remover favorito" : "Add / remove favorite";
        public static string KeysFavorite => Pt ? "Ctrl+D, ou clique direito num canal" : "Ctrl+D, or right-click a channel";
        public static string ActionChannelOptionsMenu => Pt ? "Menu de opções do canal" : "Channel options menu";
        public static string KeysChannelOptionsMenu => Pt ? "Clique direito num canal" : "Right-click a channel";
        public static string ActionSwitchPlaylist => Pt ? "Mudar de lista" : "Switch playlist";
        public static string KeysSwitchPlaylist => Pt ? "Seletor na barra superior" : "Picker in the top bar";
        public static string ActionOpenTvGuide => Pt ? "Abrir o Guia TV" : "Open the TV Guide";
        public static string KeysOpenTvGuide => Pt ? "G, ou o botão Guia TV" : "G, or the TV Guide button";

        // ---- EpgGuideWindow (TV Guide grid) -----------------------------------------------------
        public static string TvGuideTitle => Pt ? "Guia TV" : "TV Guide";
        public static string ReloadGuideButton => Pt ? "Recarregar guia" : "Reload guide";
        public static string NowButton => Pt ? "Agora" : "Now";
        public static string EarlierButton => Pt ? "Mais cedo" : "Earlier";
        public static string EarlierButtonTooltip => Pt ? "Mover o guia para mais cedo (PgUp)" : "Move the guide window earlier (PgUp)";
        public static string LaterButton => Pt ? "Mais tarde" : "Later";
        public static string LaterButtonTooltip => Pt ? "Mover o guia para mais tarde (PgDn)" : "Move the guide window later (PgDn)";
        public static string NoProgrammeInfo => Pt ? "Sem informação de programa" : "No programme info";

        // ---- Catch-up (Xtream only) --------------------------------------------------------------
        public static string WatchCatchupTooltip => Pt ? "Ver em diferido" : "Watch via catch-up";
        public static string BackToLiveButton => Pt ? "Voltar ao direto" : "Back to Live";
        public static string WatchingCatchupFormat => Pt ? "Em diferido: {0}" : "Watching catch-up: {0}";

        // ---- EpgSourcesWindow ---------------------------------------------------------------------
        public static string EpgSourcesTitle => Pt ? "Fontes de EPG" : "EPG sources";
        public static string EpgSourcesIntro => Pt
            ? "O guia incluído na sua lista é usado automaticamente. Adicione aqui outras fontes XMLTV para preencher canais em falta, ou para preferir outra fonte num canal específico (clique direito num canal → Atribuir EPG ao canal)."
            : "The guide bundled with your playlist is used automatically. Add extra XMLTV feeds here to fill in channels it's missing, or to prefer a different source for a specific channel (right-click a channel → Assign channel EPG).";
        public static string AddXmltvSourceHeading => Pt ? "Adicionar uma fonte XMLTV" : "Add an XMLTV source";
        public static string NameLabel => Pt ? "Nome" : "Name";
        public static string XmltvUrlLabel => Pt ? "URL XMLTV (.xml, .xml.gz, ou um feed comprimido em gzip)" : "XMLTV URL (.xml, .xml.gz, or a gzip-compressed feed)";
        public static string AddAndLoadButton => Pt ? "Adicionar e carregar" : "Add and load";
        public static string PrimarySourceName => Pt ? "Incluído com a lista" : "Included with playlist";
        /// <summary>{0} = 2, 3, ... — used instead of PrimarySourceName when the playlist bundles more than
        /// one EPG URL (a comma-separated url-tvg header), so each shows up as its own distinct source.</summary>
        public static string PrimarySourceNameNumberedFormat => Pt ? "Incluído com a lista ({0})" : "Included with playlist ({0})";
        public static string EnterNameAndUrl => Pt ? "Introduza um nome e um URL." : "Enter both a name and a URL.";
        public static string InvalidUrlMessage => Pt ? "Isso não parece ser um URL válido." : "That doesn't look like a valid URL.";
        public static string SourceLoadingStatus => Pt ? "A carregar…" : "Loading…";
        public static string SourceNotLoadedYet => Pt ? "Ainda não carregado" : "Not loaded yet";
        public static string SourceFailedToLoadFormat => Pt ? "Falha ao carregar: {0}" : "Failed to load: {0}";
        public static string SourceFailedReloadFormat => Pt
            ? "Carregou {0:N0} canais anteriormente, mas a última atualização falhou: {1}"
            : "Loaded {0:N0} channels earlier, but the last reload failed: {1}";
        public static string SourceLoadedFormat => Pt ? "Carregado — {0:N0} canais, {1:N0} programas" : "Loaded — {0:N0} channels, {1:N0} programmes";
        public static string ActivateButton => Pt ? "Ativar" : "Activate";
        public static string DeactivateButton => Pt ? "Desativar" : "Deactivate";
        public static string ActiveSourceNote => Pt
            ? "A correspondência automática usa apenas esta fonte, até a desativar."
            : "Automatic matching uses only this source, until you deactivate it.";

        // ---- SettingsWindow: Backup/Restore -----------------------------------------------------
        public static string BackupSection => Pt ? "CÓPIA DE SEGURANÇA" : "BACKUP";
        public static string BackupSectionBody => Pt
            ? "Guarde ou restaure as suas listas, favoritos e fontes/associações de EPG."
            : "Save or restore your playlists, favorites, and EPG sources/mappings.";
        public static string BackupButton => Pt ? "Cópia de segurança…" : "Backup…";
        public static string RestoreButton => Pt ? "Restaurar…" : "Restore…";
        public static string BackupDialogTitle => Pt ? "Guardar cópia de segurança" : "Save backup";
        public static string RestoreDialogTitle => Pt ? "Escolher cópia de segurança" : "Choose a backup file";
        public static string BackupSavedFormat => Pt ? "Cópia de segurança guardada em {0}" : "Backup saved to {0}";
        public static string BackupFailedFormat => Pt ? "Falha ao guardar a cópia de segurança: {0}" : "Couldn't save the backup: {0}";
        public static string RestoreConfirmTitle => Pt ? "Restaurar cópia de segurança" : "Restore backup";
        public static string RestoreConfirmMessage => Pt
            ? "Isto substitui todas as listas, favoritos e definições de EPG atuais pelo conteúdo do ficheiro escolhido. Não pode ser desfeito, a menos que tenha outra cópia de segurança.\n\nContinuar?"
            : "This replaces all of your current playlists, favorites, and EPG settings with the contents of the chosen file. This can't be undone unless you have another backup.\n\nContinue?";
        public static string RestoreDoneMessage => Pt ? "Restaurado. Reinicie o ZecaTV para concluir." : "Restored. Restart ZecaTV to finish.";
        public static string RestoreFailedFormat => Pt ? "Falha ao restaurar: {0}" : "Couldn't restore: {0}";
        public static string InvalidBackupFile => Pt
            ? "Esse ficheiro não parece ser uma cópia de segurança válida do ZecaTV."
            : "That file doesn't look like a valid ZecaTV backup.";

        // ---- EpgMappingWindow (Assign channel EPG) -----------------------------------------------
        public static string AssignChannelEpgTitle => Pt ? "Atribuir EPG ao canal" : "Assign channel EPG";
        public static string AssignChannelEpgIntro => Pt
            ? "Escolha um canal e associe-o a uma entrada específica do guia — isto substitui a correspondência automática apenas para esse canal."
            : "Pick a channel and point it at a specific guide entry — this overrides automatic matching for that channel only.";
        public static string ChannelLabel => Pt ? "Canal" : "Channel";
        public static string GuideSourceLabel => Pt ? "Fonte do guia" : "Guide source";
        public static string GuideChannelLabel => Pt ? "Canal do guia" : "Guide channel";
        public static string RemoveThisMappingButton => Pt ? "Remover esta associação" : "Remove this mapping";
        public static string SaveButton => Pt ? "Guardar" : "Save";
        public static string CurrentMappingsHeader => Pt ? "ASSOCIAÇÕES ATUAIS" : "CURRENT MAPPINGS";
        public static string NoPlaylistLoadedYet => Pt ? "Ainda não há nenhuma lista carregada — carregue uma primeiro e volte aqui." : "No playlist is loaded yet — load one first, then come back here.";
        public static string NoEpgSourcesAvailableYet => Pt ? "Ainda não há fontes de EPG disponíveis — adicione uma nas Definições primeiro." : "No EPG sources are available yet — add one from Settings first.";
        public static string LoadingSourceChannelList => Pt ? "A carregar a lista de canais desta fonte…" : "Loading this source's channel list…";
        public static string SourceHasNoGuideChannels => Pt ? "Esta fonte não tem canais de guia carregados." : "This source has no guide channels loaded.";
        public static string PrimarySourceNoGuideChannelsYet => Pt
            ? "Ainda não há canais de guia carregados para o EPG da própria lista — tente novamente quando terminar de carregar."
            : "No guide channels loaded yet for the playlist's own EPG — try again once it's finished loading.";
        public static string PickChannelSourceGuideFirst => Pt ? "Escolha primeiro um canal, uma fonte e um canal de guia." : "Pick a channel, a source and a guide channel first.";
        public static string SavedMappingFormat => Pt ? "Guardado — {0} agora usa \u201C{1}\u201D." : "Saved — {0} now uses \u201C{1}\u201D.";
        public static string RemovedMappingFormat => Pt ? "Removido — {0} volta a corresponder automaticamente." : "Removed — {0} will match automatically again.";
    }
}
