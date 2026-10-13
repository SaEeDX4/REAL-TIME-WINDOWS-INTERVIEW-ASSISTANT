namespace InterviewAssistant.Core.Languages;

/// <summary>
/// Localised strings for the critical desktop screens (live overlay, dashboard, sign-in, readiness) in the 10 launch
/// languages. Machine-quality drafts pending native review (see MULTILINGUAL_VALIDATION.md). Missing keys fall back to English.
/// </summary>
public static class UiStrings
{
    public static readonly string[] Keys =
    {
        "status_ready", "status_listening", "status_speech", "status_finalizing", "status_answering", "status_paused", "status_reconnecting",
        "status_no_audio", "status_api_error", "status_stopped",
        "question", "answer", "coach", "waiting", "type_hint", "start", "pause", "resume",
        "shorter", "technical", "example", "full", "keywords", "structure", "adaptive_concise", "adaptive_rapid", "minutes_left",
        "sign_in", "sign_out", "account", "upgrade", "manage_billing", "profiles", "interviews", "reports", "settings",
        "prepare", "start_interview", "authorized_use", "ready",
        "status_quiet", "status_answer_ready", "detail_listening", "detail_paused",
    };

    private static readonly Dictionary<string, string[]> T = new()
    {
        ["en"] = new[] { "Not listening", "Listening", "Transcribing", "Question detected", "Preparing answer", "Paused", "Reconnecting", "No interview audio",
            "Service problem", "Stopped", "QUESTION", "ANSWER", "COACH", "Waiting for the interviewer…", "Type or paste a question and press Enter", "Start listening",
            "Pause", "Resume", "Shorter", "Technical", "Example", "Full answer", "Keywords", "Structure",
            "Concise", "Rapid", "{0} min left", "Sign in", "Sign out", "Account", "Upgrade", "Manage billing",
            "Profiles", "Interviews", "Reports", "Settings", "Prepare", "Open Live Assistant", "I will use this only where I am permitted to (practice, preparation, or interviews that allow assistance).", "READY FOR INTERVIEW",
            "Listening · quiet", "Answer ready", "Listening to system audio · microphone off", "Live minutes are not being used" },
        ["es"] = new[] { "No escucha", "ESCUCHANDO", "VOZ DETECTADA", "FINALIZANDO PREGUNTA", "RESPONDIENDO", "EN PAUSA", "RECONECTANDO", "SIN AUDIO",
            "ERROR DEL SERVICIO", "DETENIDO", "PREGUNTA", "RESPUESTA", "COACH", "Esperando al entrevistador…", "Escriba o pegue una pregunta y pulse Intro", "Empezar a escuchar",
            "Pausar", "Reanudar", "Más corta", "Técnica", "Ejemplo", "Respuesta completa", "Palabras clave", "Estructura",
            "Concisa", "Rápida", "Quedan {0} min", "Iniciar sesión", "Cerrar sesión", "Cuenta", "Mejorar plan", "Gestionar facturación",
            "Perfiles", "Entrevistas", "Informes", "Configuración", "Preparar", "Abrir asistente en vivo", "Solo lo usaré donde esté permitido (práctica, preparación o entrevistas que permitan asistencia).", "LISTO PARA LA ENTREVISTA",
            "Escuchando · en silencio", "Respuesta lista", "Escuchando el audio del sistema · micrófono apagado", "No se están usando minutos en vivo" },
        ["fr"] = new[] { "Pas à l'écoute", "ÉCOUTE", "PAROLE DÉTECTÉE", "FINALISATION DE LA QUESTION", "RÉPONSE EN COURS", "EN PAUSE", "RECONNEXION", "PAS D'AUDIO",
            "ERREUR DU SERVICE", "ARRÊTÉ", "QUESTION", "RÉPONSE", "COACH", "En attente du recruteur…", "Saisissez ou collez une question puis appuyez sur Entrée", "Commencer l'écoute",
            "Pause", "Reprendre", "Plus courte", "Technique", "Exemple", "Réponse complète", "Mots-clés", "Structure",
            "Concis", "Rapide", "Encore {0} min", "Se connecter", "Se déconnecter", "Compte", "Passer à l'offre supérieure", "Gérer la facturation",
            "Profils", "Entretiens", "Rapports", "Paramètres", "Préparer", "Ouvrir l'assistant en direct", "Je l'utiliserai uniquement lorsque c'est autorisé (entraînement, préparation ou entretiens qui permettent une aide).", "PRÊT POUR L'ENTRETIEN",
            "À l'écoute · silence", "Réponse prête", "Écoute de l'audio système · micro désactivé", "Aucune minute en direct n'est utilisée" },
        ["de"] = new[] { "Hört nicht zu", "HÖRT ZU", "SPRACHE ERKANNT", "FRAGE WIRD ABGESCHLOSSEN", "ANTWORTET", "PAUSIERT", "VERBINDET NEU", "KEIN AUDIO",
            "DIENSTFEHLER", "GESTOPPT", "FRAGE", "ANTWORT", "COACH", "Warte auf den Interviewer…", "Frage eingeben oder einfügen und Enter drücken", "Zuhören starten",
            "Pause", "Fortsetzen", "Kürzer", "Technisch", "Beispiel", "Ganze Antwort", "Stichworte", "Struktur",
            "Knapp", "Schnell", "Noch {0} Min.", "Anmelden", "Abmelden", "Konto", "Upgrade", "Abrechnung verwalten",
            "Profile", "Interviews", "Berichte", "Einstellungen", "Vorbereiten", "Live-Assistent öffnen", "Ich nutze dies nur, wo es erlaubt ist (Übung, Vorbereitung oder Interviews, die Hilfsmittel zulassen).", "BEREIT FÜR DAS INTERVIEW",
            "Hört zu · still", "Antwort bereit", "Hört Systemaudio · Mikrofon aus", "Es werden keine Live-Minuten verbraucht" },
        ["pt"] = new[] { "Não está ouvindo", "OUVINDO", "FALA DETECTADA", "FINALIZANDO PERGUNTA", "RESPONDENDO", "PAUSADO", "RECONECTANDO", "SEM ÁUDIO",
            "ERRO DO SERVIÇO", "PARADO", "PERGUNTA", "RESPOSTA", "COACH", "Aguardando o entrevistador…", "Digite ou cole uma pergunta e pressione Enter", "Começar a ouvir",
            "Pausar", "Retomar", "Mais curta", "Técnica", "Exemplo", "Resposta completa", "Palavras-chave", "Estrutura",
            "Concisa", "Rápida", "Restam {0} min", "Entrar", "Sair", "Conta", "Fazer upgrade", "Gerenciar cobrança",
            "Perfis", "Entrevistas", "Relatórios", "Configurações", "Preparar", "Abrir assistente ao vivo", "Vou usar apenas onde for permitido (prática, preparação ou entrevistas que permitam assistência).", "PRONTO PARA A ENTREVISTA",
            "Ouvindo · silêncio", "Resposta pronta", "Ouvindo o áudio do sistema · microfone desligado", "Nenhum minuto ao vivo está sendo usado" },
        ["it"] = new[] { "Non in ascolto", "IN ASCOLTO", "VOCE RILEVATA", "DOMANDA IN COMPLETAMENTO", "RISPOSTA IN CORSO", "IN PAUSA", "RICONNESSIONE", "NESSUN AUDIO",
            "ERRORE DEL SERVIZIO", "FERMATO", "DOMANDA", "RISPOSTA", "COACH", "In attesa dell'intervistatore…", "Scrivi o incolla una domanda e premi Invio", "Inizia ad ascoltare",
            "Pausa", "Riprendi", "Più breve", "Tecnica", "Esempio", "Risposta completa", "Parole chiave", "Struttura",
            "Concisa", "Rapida", "Mancano {0} min", "Accedi", "Esci", "Account", "Passa a un piano superiore", "Gestisci fatturazione",
            "Profili", "Colloqui", "Report", "Impostazioni", "Prepara", "Apri assistente live", "Lo userò solo dove è consentito (pratica, preparazione o colloqui che ammettono assistenza).", "PRONTO PER IL COLLOQUIO",
            "In ascolto · silenzio", "Risposta pronta", "Ascolto dell'audio di sistema · microfono spento", "Nessun minuto live viene consumato" },
        ["ar"] = new[] { "لا يستمع", "يستمع", "تم رصد كلام", "يجري إنهاء السؤال", "يجيب", "متوقف مؤقتًا", "يعيد الاتصال", "لا يوجد صوت",
            "خطأ في الخدمة", "متوقف", "السؤال", "الإجابة", "المدرّب", "في انتظار المحاور…", "اكتب سؤالًا أو الصقه ثم اضغط Enter", "ابدأ الاستماع",
            "إيقاف مؤقت", "استئناف", "أقصر", "تقني", "مثال", "إجابة كاملة", "كلمات مفتاحية", "الهيكل",
            "موجز", "سريع", "متبقٍ {0} دقيقة", "تسجيل الدخول", "تسجيل الخروج", "الحساب", "ترقية", "إدارة الفوترة",
            "الملفات الشخصية", "المقابلات", "التقارير", "الإعدادات", "تحضير", "فتح المساعد المباشر", "سأستخدمه فقط حيث يُسمح بذلك (التدريب أو التحضير أو المقابلات التي تسمح بالمساعدة).", "جاهز للمقابلة",
            "يستمع · هدوء", "الإجابة جاهزة", "يستمع إلى صوت النظام · الميكروفون مغلق", "لا يتم استهلاك دقائق مباشرة" },
        ["fa"] = new[] { "در حال شنیدن نیست", "در حال شنیدن", "صدا شناسایی شد", "در حال نهایی‌کردن پرسش", "در حال پاسخ", "متوقف موقت", "اتصال دوباره", "بدون صدا",
            "خطای سرویس", "متوقف", "پرسش", "پاسخ", "مربی", "در انتظار مصاحبه‌گر…", "پرسشی بنویسید یا بچسبانید و Enter را بزنید", "شروع شنیدن",
            "توقف موقت", "ادامه", "کوتاه‌تر", "فنی", "مثال", "پاسخ کامل", "کلیدواژه‌ها", "ساختار",
            "خلاصه", "سریع", "{0} دقیقه باقی مانده", "ورود", "خروج", "حساب کاربری", "ارتقا", "مدیریت پرداخت",
            "پروفایل‌ها", "مصاحبه‌ها", "گزارش‌ها", "تنظیمات", "آماده‌سازی", "باز کردن دستیار زنده", "فقط در جایی از آن استفاده می‌کنم که مجاز باشد (تمرین، آمادگی یا مصاحبه‌هایی که کمک را مجاز می‌دانند).", "آماده برای مصاحبه",
            "در حال شنیدن · سکوت", "پاسخ آماده است", "در حال شنیدن صدای سیستم · میکروفون خاموش", "دقیقه‌های زنده مصرف نمی‌شود" },
        ["zh"] = new[] { "未在聆听", "正在聆听", "检测到语音", "正在确定问题", "正在回答", "已暂停", "正在重新连接", "无音频",
            "服务错误", "已停止", "问题", "回答", "教练", "等待面试官…", "输入或粘贴问题后按回车", "开始聆听",
            "暂停", "继续", "更简短", "技术向", "举例", "完整回答", "关键词", "结构",
            "简洁", "快速", "剩余 {0} 分钟", "登录", "退出登录", "账户", "升级", "管理账单",
            "个人资料", "面试", "报告", "设置", "准备", "打开实时助手", "我只会在允许的情况下使用（练习、准备或允许辅助的面试）。", "已准备好面试",
            "正在聆听 · 安静", "答案已就绪", "正在聆听系统音频 · 麦克风关闭", "不消耗实时分钟数" },
        ["hi"] = new[] { "नहीं सुन रहा", "सुन रहा है", "आवाज़ मिली", "प्रश्न पूरा हो रहा है", "उत्तर दे रहा है", "रुका हुआ", "फिर से जुड़ रहा है", "कोई ऑडियो नहीं",
            "सेवा त्रुटि", "बंद", "प्रश्न", "उत्तर", "कोच", "इंटरव्यूअर की प्रतीक्षा…", "प्रश्न लिखें या चिपकाएँ और Enter दबाएँ", "सुनना शुरू करें",
            "रोकें", "फिर शुरू करें", "छोटा", "तकनीकी", "उदाहरण", "पूरा उत्तर", "मुख्य शब्द", "संरचना",
            "संक्षिप्त", "तेज़", "{0} मिनट बाकी", "साइन इन", "साइन आउट", "खाता", "अपग्रेड", "बिलिंग प्रबंधित करें",
            "प्रोफ़ाइल", "इंटरव्यू", "रिपोर्ट", "सेटिंग्स", "तैयारी करें", "लाइव सहायक खोलें", "मैं इसका उपयोग केवल वहीं करूँगा जहाँ इसकी अनुमति है (अभ्यास, तैयारी या ऐसे इंटरव्यू जिनमें सहायता की अनुमति है)।", "इंटरव्यू के लिए तैयार",
            "सुन रहा है · शांत", "उत्तर तैयार है", "सिस्टम ऑडियो सुन रहा है · माइक्रोफ़ोन बंद", "लाइव मिनट खर्च नहीं हो रहे हैं" },
    };

    public static IReadOnlyCollection<string> Languages => T.Keys;

    public static string Get(string? lang, string key)
    {
        var code = LanguageRegistry.Find(lang)?.Code ?? "en";
        var i = Array.IndexOf(Keys, key);
        if (i < 0) return key;
        var arr = T.GetValueOrDefault(code) ?? T["en"];
        return i < arr.Length && !string.IsNullOrWhiteSpace(arr[i]) ? arr[i] : T["en"][i];
    }

    public static string Format(string? lang, string key, params object[] args) => string.Format(System.Globalization.CultureInfo.InvariantCulture, Get(lang, key), args);

    public static bool IsComplete(string lang) => T.TryGetValue(lang, out var a) && a.Length == Keys.Length && a.All(s => !string.IsNullOrWhiteSpace(s));
}
