Attribute VB_Name = "Module1"
Option Explicit

Private Const SURUM As String = "2026.10.21-v9"
Private Const KUR_URL As String = "https://www.isbank.com.tr/doviz-kurlari"
Private Const WHATSAPP_BEKLEME_SANIYE As Long = 25
Private Const SIFRE As String = ""

#If VBA7 Then
Private Declare PtrSafe Function GetWindow Lib "user32" (ByVal hWnd As LongPtr, ByVal wCmd As Long) As LongPtr
Private Declare PtrSafe Function GetDesktopWindow Lib "user32" () As LongPtr
Private Declare PtrSafe Function GetWindowTextW Lib "user32" (ByVal hWnd As LongPtr, ByVal lpString As LongPtr, ByVal cch As Long) As Long
Private Declare PtrSafe Function IsWindowVisible Lib "user32" (ByVal hWnd As LongPtr) As Long
Private Declare PtrSafe Function IsIconic Lib "user32" (ByVal hWnd As LongPtr) As Long
Private Declare PtrSafe Function ShowWindow Lib "user32" (ByVal hWnd As LongPtr, ByVal nCmdShow As Long) As Long
Private Declare PtrSafe Function SetForegroundWindow Lib "user32" (ByVal hWnd As LongPtr) As Long
#Else
Private Declare Function GetWindow Lib "user32" (ByVal hWnd As Long, ByVal wCmd As Long) As Long
Private Declare Function GetDesktopWindow Lib "user32" () As Long
Private Declare Function GetWindowTextW Lib "user32" (ByVal hWnd As Long, ByVal lpString As Long, ByVal cch As Long) As Long
Private Declare Function IsWindowVisible Lib "user32" (ByVal hWnd As Long) As Long
Private Declare Function IsIconic Lib "user32" (ByVal hWnd As Long) As Long
Private Declare Function ShowWindow Lib "user32" (ByVal hWnd As Long, ByVal nCmdShow As Long) As Long
Private Declare Function SetForegroundWindow Lib "user32" (ByVal hWnd As Long) As Long
#End If

Rem ===== ORTAK YARDIMCILAR =====

Private Function TeklifSayfasi() As Worksheet
    Set TeklifSayfasi = ThisWorkbook.Worksheets("Teklif Formu")
End Function

Private Function AyarlarSayfasi() As Worksheet
    Set AyarlarSayfasi = ThisWorkbook.Worksheets("Ayarlar")
End Function

Private Function ArsivTablosu() As ListObject
    Set ArsivTablosu = ThisWorkbook.Worksheets("Teklif Arşivi").ListObjects("tblTeklifArsivi")
End Function

Private Function GunlukTablosu() As ListObject
    Set GunlukTablosu = ThisWorkbook.Worksheets("İşlem Günlüğü").ListObjects("tblIslemGunlugu")
End Function

' Hücrenin .Text değeri dar sütunda "####" döner; numara her zaman .Value'dan okunur.
Private Function AktifTeklifNo() As String
    AktifTeklifNo = Trim$(CStr(TeklifSayfasi.Range("J3").Value))
End Function

Private Function Sayi(ByVal v As Variant) As Double
    If IsError(v) Then Exit Function
    If IsNumeric(v) Then Sayi = CDbl(v)
End Function

Private Sub KlasorOlustur(ByVal yol As String)
    Dim parca As Variant, birikim As String, i As Long
    If Len(yol) = 0 Then Exit Sub
    If Len(Dir$(yol, vbDirectory)) > 0 Then Exit Sub
    parca = Split(yol, "\")
    If UBound(parca) < 0 Then Exit Sub
    birikim = parca(0)
    For i = 1 To UBound(parca)
        birikim = birikim & "\" & parca(i)
        If Len(Dir$(birikim, vbDirectory)) = 0 Then MkDir birikim
    Next i
End Sub

Private Function DosyaAdiTemizle(ByVal metin As String) As String
    Dim yasak As Variant, x As Variant, i As Long
    yasak = Array("\", "/", ":", "*", "?", Chr$(34), "<", ">", "|")
    For Each x In yasak
        metin = Replace(metin, CStr(x), "-")
    Next x
    For i = 0 To 31
        metin = Replace(metin, Chr$(i), "")
    Next i
    metin = Trim$(metin)
    If Len(metin) = 0 Then metin = "Numarasiz"
    If Len(metin) > 120 Then metin = Left$(metin, 120)
    DosyaAdiTemizle = metin
End Function

Private Function MasaustuPDFKlasoru() As String
    Dim masaustu As String
    masaustu = CreateObject("WScript.Shell").SpecialFolders("Desktop")
    If Len(masaustu) = 0 Then Err.Raise vbObjectError + 301, , "Masaüstü yolu alınamadı."
    KlasorOlustur masaustu & "\Teklifler"
    KlasorOlustur masaustu & "\Teklifler\PDF"
    MasaustuPDFKlasoru = masaustu & "\Teklifler\PDF"
End Function

Private Sub SayfaDuzeniAyarla(ByVal ws As Worksheet)
    On Error GoTo Temizle
    Application.PrintCommunication = False
    With ws.PageSetup
        .PrintArea = "$A$1:$K$40"
        .Orientation = xlPortrait
        .PaperSize = xlPaperA4
        .Zoom = False
        .FitToPagesWide = 1
        .FitToPagesTall = 1
        .CenterHorizontally = True
        .LeftMargin = Application.CentimetersToPoints(0.6)
        .RightMargin = Application.CentimetersToPoints(0.6)
        .TopMargin = Application.CentimetersToPoints(0.6)
        .BottomMargin = Application.CentimetersToPoints(0.6)
    End With
Temizle:
    On Error Resume Next
    Application.PrintCommunication = True
End Sub

' Tüm PDF işlemleri (Kaydet, Outlook, WhatsApp, test) aynı sayfa düzeniyle bu fonksiyonu kullanır.
Public Function TeklifPDFOlustur(ByVal klasor As String, Optional ByVal onEk As String = "Teklif") As String
    Dim ws As Worksheet, yol As String
    Set ws = TeklifSayfasi
    KlasorOlustur klasor
    yol = klasor & "\" & onEk & "_" & DosyaAdiTemizle(AktifTeklifNo()) & "_" & Format$(Now, "yyyymmdd_hhnnss") & ".pdf"
    SayfaDuzeniAyarla ws
    ws.ExportAsFixedFormat Type:=xlTypePDF, Filename:=yol, Quality:=xlQualityStandard, _
        IncludeDocProperties:=True, IgnorePrintAreas:=False, OpenAfterPublish:=False
    If Len(Dir$(yol)) = 0 Then Err.Raise vbObjectError + 302, , "PDF oluşturulamadı: " & yol
    TeklifPDFOlustur = yol
End Function

Private Function MusteriBilgisi(ByVal firma As String, ByVal sutun As String) As String
    Dim t As ListObject, r As Variant
    On Error GoTo Son
    If Len(Trim$(firma)) = 0 Then Exit Function
    Set t = ThisWorkbook.Worksheets("Müşteriler").ListObjects("tblMusteriler")
    If t.DataBodyRange Is Nothing Then Exit Function
    r = Application.Match(firma, t.ListColumns("Firma").DataBodyRange, 0)
    If Not IsError(r) Then MusteriBilgisi = Trim$(CStr(t.ListColumns(sutun).DataBodyRange.Cells(CLng(r), 1).Value))
Son:
End Function

Private Function UrlEncode(ByVal metin As String) As String
    On Error GoTo Elle
    UrlEncode = Application.WorksheetFunction.EncodeURL(metin)
    Exit Function
Elle:
    Dim i As Long, ch As String, out As String
    For i = 1 To Len(metin)
        ch = Mid$(metin, i, 1)
        Select Case AscW(ch)
            Case 48 To 57, 65 To 90, 97 To 122, 45, 46, 95, 126
                out = out & ch
            Case 32
                out = out & "%20"
            Case Else
                out = out & "%" & Right$("0" & Hex$(AscW(ch) And &HFF&), 2)
        End Select
    Next i
    UrlEncode = out
End Function

' Tabloda yalnızca boş başlangıç satırı varsa onu kullanır; aksi halde yeni satır ekler.
Private Function TabloSatiriAl(ByVal t As ListObject) As Range
    If t.ListRows.Count = 1 Then
        If Not t.DataBodyRange Is Nothing Then
            If Application.WorksheetFunction.CountA(t.DataBodyRange) = 0 Then
                Set TabloSatiriAl = t.DataBodyRange.Rows(1)
                Exit Function
            End If
        End If
    End If
    Set TabloSatiriAl = t.ListRows.Add.Range
End Function

Rem ===== GÖNDERİM ÖNCESİ KONTROL =====

Public Function GonderimUyarilari() As String
    Dim ws As Worksheet, a As Worksheet, s As String, r As Long
    Dim sonKur As Double, esik As Double, dovizVar As Boolean
    Set ws = TeklifSayfasi
    Set a = AyarlarSayfasi
    If Len(Trim$(CStr(ws.Range("D3").Value))) = 0 Then s = s & "• Müşteri seçilmemiş." & vbCrLf
    If Application.WorksheetFunction.CountA(ws.Range("A8:A30")) = 0 Then s = s & "• Teklifte ürün kalemi yok." & vbCrLf
    For r = 8 To 30
        If Len(Trim$(CStr(ws.Cells(r, 1).Value))) > 0 Then
            If Sayi(ws.Cells(r, 4).Value) <= 0 Or Sayi(ws.Cells(r, 5).Value) <= 0 Then s = s & "• " & r & ". satırda miktar veya birim fiyat eksik." & vbCrLf
            If UCase$(CStr(ws.Cells(r, 9).Value)) <> "TRY" Then dovizVar = True
        End If
    Next r
    If IsDate(ws.Range("J5").Value) Then
        If CDate(ws.Range("J5").Value) < Date Then s = s & "• Teklifin geçerlilik tarihi geçmiş." & vbCrLf
    End If
    If dovizVar Then
        On Error Resume Next
        sonKur = Application.WorksheetFunction.Max(ThisWorkbook.Worksheets("Kur Bilgisi").Range("D7:D9"))
        On Error GoTo 0
        esik = Sayi(a.Range("B14").Value)
        If esik <= 0 Then esik = 12
        If sonKur = 0 Or (Now - sonKur) * 24 > esik Then s = s & "• Döviz kurları güncel değil (son güncelleme: " & IIf(sonKur = 0, "yok", Format$(sonKur, "dd.mm.yyyy hh:nn")) & ")." & vbCrLf
    End If
    If Sayi(a.Range("B12").Value) > 0 And Sayi(ws.Range("K33").Value) >= Sayi(a.Range("B12").Value) Then s = s & "• Tutar yönetici onay eşiğinin üzerinde." & vbCrLf
    If Sayi(a.Range("B9").Value) > Sayi(a.Range("B13").Value) Then s = s & "• İskonto oranı onay eşiğinin üzerinde." & vbCrLf
    GonderimUyarilari = s
End Function

Private Function GonderimOnayi(ByVal islem As String) As Boolean
    Dim s As String
    s = GonderimUyarilari()
    If Len(s) = 0 Then
        GonderimOnayi = True
    Else
        GonderimOnayi = (MsgBox(islem & " öncesi kontrol uyarıları:" & vbCrLf & vbCrLf & s & vbCrLf & "Yine de devam edilsin mi?", _
            vbYesNo + vbExclamation + vbDefaultButton2, "Gönderim Kontrolü " & SURUM) = vbYes)
    End If
End Function

Rem ===== DÖVİZ KURLARI =====

Private Function KurCekirdek(ByVal Sessiz As Boolean) As Boolean
    On Error GoTo Hata
    Dim req As Object, ws As Worksheet, html As String
    Dim kodlar As Variant, kurlar(0 To 2) As Double, satir As Variant, i As Long

    DurumCubugu "İş Bankası döviz satış kurları alınıyor..."
    Set ws = ThisWorkbook.Worksheets("Kur Bilgisi")
    Set req = CreateObject("MSXML2.ServerXMLHTTP.6.0")
    req.setTimeouts 5000, 5000, 10000, 15000
    req.Open "GET", KUR_URL, False
    req.setRequestHeader "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"
    req.setRequestHeader "Accept", "text/html"
    req.send
    If req.Status <> 200 Then Err.Raise vbObjectError + 100, , "HTTP hatası: " & req.Status
    html = CStr(req.responseText)

    kodlar = Array("USD", "EUR", "GBP")
    For i = LBound(kodlar) To UBound(kodlar)
        kurlar(i) = SatisKuruBulV6(html, CStr(kodlar(i)))
        If kurlar(i) < 1 Or kurlar(i) > 1000 Then Err.Raise vbObjectError + 101, , kodlar(i) & " satış kuru sayfada bulunamadı (sayfa yapısı değişmiş olabilir)."
    Next i

    For i = LBound(kodlar) To UBound(kodlar)
        satir = Application.Match(kodlar(i), ws.Range("A:A"), 0)
        If IsError(satir) Then Err.Raise vbObjectError + 102, , kodlar(i) & " satırı Kur Bilgisi sayfasında yok."
        ws.Cells(CLng(satir), 3).Value = kurlar(i)
        ws.Cells(CLng(satir), 4).Value = Now
        ws.Cells(CLng(satir), 5).Value = "İş Bankası"
        ws.Cells(CLng(satir), 6).Value = "Otomatik yenilendi " & SURUM
    Next i
    ws.Range("D6:D9").NumberFormat = "dd.mm.yyyy hh:mm"
    Hesapla
    DurumCubugu False
    KurCekirdek = True
    If Not Sessiz Then MsgBox "USD, EUR ve GBP satış kurları güncellendi." & vbCrLf & _
        "USD: " & Format$(kurlar(0), "0.0000") & "  EUR: " & Format$(kurlar(1), "0.0000") & "  GBP: " & Format$(kurlar(2), "0.0000"), vbInformation, "Kur " & SURUM
    Exit Function
Hata:
    Dim hataMetni As String
    hataMetni = Err.Description
    DurumCubugu False
    On Error Resume Next
    ' Eski kurlar korunur; tarih sütunu değişmediği için Teklif Yönetimi panosu "YENİLE" gösterir.
    ThisWorkbook.Worksheets("Kur Bilgisi").Range("F7:F9").Value = "Güncellenemedi " & Format$(Now, "dd.mm.yyyy hh:nn") & ": " & hataMetni
    If Not Sessiz Then MsgBox "Kur güncellenemedi; mevcut kurlar korundu." & vbCrLf & "Hata: " & hataMetni, vbExclamation, "Kur " & SURUM
End Function

Private Function SatisKuruBulV6(ByVal html As String, ByVal kod As String) As Double
    Dim re As Object, ms As Object, m As Object
    Dim alis As Double, satis As Double
    Set re = CreateObject("VBScript.RegExp")
    With re
        .Global = True
        .IgnoreCase = True
        .MultiLine = True
        .Pattern = "\b" & kod & "\b[\s\S]{0,500}?([0-9]{1,3}[.,][0-9]{2,5})[\s\S]{0,200}?([0-9]{1,3}[.,][0-9]{2,5})"
    End With
    Set ms = re.Execute(html)
    For Each m In ms
        alis = YerelSayiyaCevirV3(CStr(m.SubMatches(0)))
        satis = YerelSayiyaCevirV3(CStr(m.SubMatches(1)))
        If alis > 1 And satis > alis And satis < 1000 And satis / alis < 1.25 Then
            SatisKuruBulV6 = satis
            Exit Function
        End If
    Next m
End Function

' Val() her zaman noktayı ondalık ayıracı kabul eder; Windows/Excel bölge ayarından etkilenmez.
Private Function YerelSayiyaCevirV3(ByVal metin As String) As Double
    Dim s As String
    s = Replace(Replace(Replace(Trim$(metin), "TL", ""), Chr$(160), ""), " ", "")
    If InStr(s, ",") > 0 Then s = Replace(Replace(s, ".", ""), ",", ".")
    If Len(s) > 0 And Not s Like "*[!0-9.]*" Then YerelSayiyaCevirV3 = Val(s)
End Function

Public Sub IsBankasiKurlariGuncelleV3()
    KurCekirdek False
End Sub

Public Sub IsBankasiKurlariOtomatikV6(Optional ByVal Sessiz As Boolean = True)
    KurCekirdek Sessiz
End Sub

Public Sub KurlariSessizGuncelle()
    KurCekirdek True
End Sub

Rem ===== ÜRÜN VARSAYILANLARI =====

' Teklif Formu'nda ürün seçildiğinde Ürünler tablosundaki standart fiyat, para birimi ve KDV boş satıra yazılır.
Public Sub UrunVarsayilanlariniUygula(ByVal hucre As Range)
    Dim t As ListObject, r As Variant, urun As String, satir As Long, ws As Worksheet
    On Error GoTo Son
    urun = Trim$(CStr(hucre.Value))
    If Len(urun) = 0 Then Exit Sub
    Set ws = hucre.Worksheet
    satir = hucre.Row
    Set t = ThisWorkbook.Worksheets("Urunler").ListObjects("tblUrunler")
    If t.DataBodyRange Is Nothing Then Exit Sub
    r = Application.Match(urun, t.ListColumns("Urun").DataBodyRange, 0)
    If IsError(r) Then Exit Sub
    If Len(CStr(ws.Cells(satir, 4).Value)) = 0 Then ws.Cells(satir, 4).Value = 1
    If Len(CStr(ws.Cells(satir, 5).Value)) = 0 Then
        If Sayi(t.ListColumns("Standart Birim Fiyat").DataBodyRange.Cells(CLng(r), 1).Value) > 0 Then
            ws.Cells(satir, 5).Value = t.ListColumns("Standart Birim Fiyat").DataBodyRange.Cells(CLng(r), 1).Value
        End If
        If Len(CStr(t.ListColumns("Para Birimi").DataBodyRange.Cells(CLng(r), 1).Value)) > 0 Then
            ws.Cells(satir, 9).Value = t.ListColumns("Para Birimi").DataBodyRange.Cells(CLng(r), 1).Value
        End If
    End If
    If Len(CStr(t.ListColumns("KDV").DataBodyRange.Cells(CLng(r), 1).Value)) > 0 Then
        ws.Cells(satir, 7).Value = t.ListColumns("KDV").DataBodyRange.Cells(CLng(r), 1).Value
    End If
Son:
End Sub

Rem ===== WHATSAPP =====

Public Sub TeklifiWhatsAppPDFGonderV5()
    On Error GoTo Hata
    Dim ws As Worksheet, tel As String, pdf As String, varsayilanTel As String
    Dim mesaj As String, url As String, yetkili As String, asama As String, bekle As Single

    pdf = ""
    asama = "Teklif kontrol ediliyor"
    Set ws = TeklifSayfasi
    If Not GonderimOnayi("WhatsApp gönderimi") Then Exit Sub
    varsayilanTel = MusteriBilgisi(CStr(ws.Range("D3").Value), "Telefon")
    tel = TemizTelefonV5(InputBox("Alıcı telefonunu ülke koduyla girin (örn. 905xxxxxxxxx):", "WhatsApp PDF Paylaşımı " & SURUM, varsayilanTel))
    If Len(tel) = 0 Then Exit Sub
    If Len(tel) < 10 Or Len(tel) > 15 Then Err.Raise vbObjectError + 201, , "Telefon numarası geçersiz."
    If MsgBox("Teklif PDF olarak oluşturulup +" & tel & " numarasına gönderilecek. Devam edilsin mi?", vbYesNo + vbQuestion, "Onay " & SURUM) <> vbYes Then Exit Sub

    asama = "A4 PDF oluşturuluyor"
    pdf = TeklifPDFOlustur(MasaustuPDFKlasoru())

    asama = "PDF Windows panosuna alınıyor"
    DosyayiPanoyaKopyalaV5 pdf

    asama = "WhatsApp Web açılıyor"
    yetkili = Trim$(CStr(ws.Range("D4").Value))
    mesaj = IIf(Len(yetkili) > 0, "Sayın " & yetkili & ", ", "Merhaba, ") & AktifTeklifNo() & " numaralı teklifimiz ektedir."
    url = "https://web.whatsapp.com/send?phone=" & tel & "&text=" & UrlEncode(mesaj)
    ThisWorkbook.FollowHyperlink Address:=url, NewWindow:=True

    asama = "WhatsApp penceresi bekleniyor"
    bekle = Timer
    Do
        Application.Wait Now + TimeSerial(0, 0, 1)
        If PencereyiOneGetir("WhatsApp") Then Exit Do
        If Timer - bekle > WHATSAPP_BEKLEME_SANIYE Or Timer < bekle Then Err.Raise vbObjectError + 205, , "WhatsApp Web penceresi bulunamadı. Tarayıcıda WhatsApp Web'e giriş yapıp tekrar deneyin."
    Loop
    ' Sohbetin yüklenmesi ve mesaj kutusunun odaklanması için bekleme.
    Application.Wait Now + TimeSerial(0, 0, 8)
    PencereyiOneGetir "WhatsApp"

    asama = "PDF WhatsApp'a yapıştırılıyor"
    With CreateObject("WScript.Shell")
        .SendKeys "^v", True
        Application.Wait Now + TimeSerial(0, 0, 4)
        .SendKeys "{ENTER}", True
    End With

    TeklifIslemLoguV2 "WhatsApp gönderildi", AktifTeklifNo(), "Kontrol edilmeli", pdf
    MsgBox "PDF WhatsApp'a yapıştırıldı ve gönder komutu verildi." & vbCrLf & _
        "Lütfen WhatsApp penceresinde gönderildiğini kontrol edin." & vbCrLf & vbCrLf & pdf, vbInformation, "WhatsApp " & SURUM
    Exit Sub
Hata:
    MsgBox "WhatsApp paylaşımı tamamlanamadı." & vbCrLf & "Aşama: " & asama & vbCrLf & _
        "Hata " & Err.Number & ": " & Err.Description & IIf(Len(pdf) > 0, vbCrLf & "PDF yolu: " & pdf, ""), vbExclamation, "WhatsApp " & SURUM
End Sub

#If VBA7 Then
Private Function PencereBasligi(ByVal h As LongPtr) As String
#Else
Private Function PencereBasligi(ByVal h As Long) As String
#End If
    Dim b As String, n As Long
    b = String$(512, vbNullChar)
    n = GetWindowTextW(h, StrPtr(b), 512)
    If n > 0 Then PencereBasligi = Left$(b, n)
End Function

' Başlığında aranan metin geçen ilk görünür pencereyi öne getirir ("(3) WhatsApp - Chrome" gibi başlıklar dahil).
Private Function PencereyiOneGetir(ByVal arananMetin As String) As Boolean
#If VBA7 Then
    Dim h As LongPtr
#Else
    Dim h As Long
#End If
    h = GetWindow(GetDesktopWindow(), 5)
    Do While h <> 0
        If IsWindowVisible(h) <> 0 Then
            If InStr(1, PencereBasligi(h), arananMetin, vbTextCompare) > 0 Then
                If IsIconic(h) <> 0 Then ShowWindow h, 9
                SetForegroundWindow h
                PencereyiOneGetir = True
                Exit Function
            End If
        End If
        h = GetWindow(h, 2)
    Loop
End Function

Private Sub DosyayiPanoyaKopyalaV5(ByVal pdf As String)
    Dim komut As String, sonuc As Long
    If Len(Dir$(pdf)) = 0 Then Err.Raise vbObjectError + 206, , "Panoya alınacak PDF bulunamadı: " & pdf
    komut = "powershell.exe -NoProfile -STA -Command ""Add-Type -AssemblyName System.Windows.Forms; " & _
        "$f=New-Object System.Collections.Specialized.StringCollection; [void]$f.Add('" & Replace(pdf, "'", "''") & "'); " & _
        "[System.Windows.Forms.Clipboard]::SetFileDropList($f)"""
    sonuc = CreateObject("WScript.Shell").Run(komut, 0, True)
    If sonuc <> 0 Then Err.Raise vbObjectError + 208, , "PDF panoya alınamadı. PowerShell çıkış kodu: " & sonuc
    DoEvents
End Sub

Private Function TemizTelefonV5(ByVal metin As String) As String
    Dim i As Long, ch As String, sonuc As String
    For i = 1 To Len(metin)
        ch = Mid$(metin, i, 1)
        If ch Like "#" Then sonuc = sonuc & ch
    Next i
    If Left$(sonuc, 2) = "00" Then sonuc = Mid$(sonuc, 3)
    If Left$(sonuc, 2) = "05" And Len(sonuc) = 11 Then sonuc = "90" & Mid$(sonuc, 2)
    If Len(sonuc) = 10 And Left$(sonuc, 1) = "5" Then sonuc = "90" & sonuc
    TemizTelefonV5 = sonuc
End Function

Rem ===== PDF =====

Public Sub TeklifiMasaustunePDFKaydetV2()
    On Error GoTo Hata
    Dim yol As String
    If Not GonderimOnayi("PDF kaydı") Then Exit Sub
    yol = TeklifPDFOlustur(MasaustuPDFKlasoru())
    TeklifIslemLoguV2 "PDF kaydedildi", AktifTeklifNo(), "Başarılı", yol
    CreateObject("Shell.Application").Open MasaustuPDFKlasoru()
    MsgBox "Teklif PDF olarak kaydedildi." & vbCrLf & yol, vbInformation, "PDF " & SURUM
    Exit Sub
Hata:
    MsgBox "PDF kaydedilemedi." & vbCrLf & "Hata " & Err.Number & ": " & Err.Description, vbExclamation, "PDF " & SURUM
End Sub

Public Sub PDFDugmesiniBaglaV2()
    On Error GoTo Hata
    ThisWorkbook.Worksheets("PDF Kaydet").Shapes("PDF Kaydet Düğmesi").OnAction = "'" & ThisWorkbook.Name & "'!TeklifiMasaustunePDFKaydetV2"
    MsgBox "PDF Kaydet düğmesi hazır.", vbInformation
    Exit Sub
Hata:
    MsgBox "Düğme bağlanamadı: " & Err.Description, vbExclamation
End Sub

Public Sub PDFKaydetTestiV2()
    On Error GoTo Hata
    Dim yol As String
    yol = TeklifPDFOlustur(MasaustuPDFKlasoru(), "Teklif_TEST")
    CreateObject("Shell.Application").Open MasaustuPDFKlasoru()
    MsgBox "PDF kayıt testi başarılı." & vbCrLf & yol, vbInformation
    Exit Sub
Hata:
    MsgBox "PDF kayıt testi başarısız. Hata " & Err.Number & ": " & Err.Description, vbExclamation
End Sub

Rem ===== OUTLOOK =====

Public Sub TeklifiOutlookPDFTaslakV1()
    On Error GoTo Hata
    Dim ws As Worksheet, outlookApp As Object, posta As Object
    Dim tamYol As String, asama As String, no As String, musteri As String, yetkili As String
    Dim govde As String, html As String, p As Long

    asama = "Teklif kontrol ediliyor"
    Set ws = TeklifSayfasi
    If Not GonderimOnayi("Outlook e-postası") Then Exit Sub
    no = AktifTeklifNo()
    musteri = Trim$(CStr(ws.Range("D3").Value))
    yetkili = Trim$(CStr(ws.Range("D4").Value))

    asama = "A4 PDF oluşturuluyor"
    tamYol = TeklifPDFOlustur(MasaustuPDFKlasoru())

    asama = "Klasik Outlook açılıyor"
    On Error Resume Next
    Set outlookApp = GetObject(, "Outlook.Application")
    If outlookApp Is Nothing Then Set outlookApp = CreateObject("Outlook.Application")
    On Error GoTo Hata
    If outlookApp Is Nothing Then Err.Raise vbObjectError + 403, , "Klasik Outlook başlatılamadı. Yeni Outlook kullanıyorsanız Klasik Outlook'a geçin."

    asama = "Yeni e-posta hazırlanıyor"
    Set posta = outlookApp.CreateItem(0)
    If posta Is Nothing Then Err.Raise vbObjectError + 404, , "Yeni e-posta oluşturulamadı."
    posta.To = MusteriBilgisi(musteri, "E-posta")
    posta.Subject = "Teklif " & no & IIf(Len(musteri) > 0, " - " & musteri, "")
    posta.Attachments.Add tamYol, 1
    posta.Display

    ' Gövde, imza korunarak <body> etiketinin hemen arkasına eklenir.
    govde = "<p>Sayın " & HtmlKacis(IIf(Len(yetkili) > 0, yetkili, "Yetkili")) & ",</p>" & _
        "<p>" & HtmlKacis(no) & " numaralı teklifimizi ekte bilgilerinize sunarız.</p>" & _
        "<p>Genel toplam (KDV dahil): " & Format$(Sayi(ws.Range("K33").Value), "#,##0.00") & " TL"
    If IsDate(ws.Range("J5").Value) Then govde = govde & "<br>Geçerlilik tarihi: " & Format$(ws.Range("J5").Value, "dd.mm.yyyy")
    govde = govde & "</p><p>Saygılarımızla,</p>"
    html = posta.HTMLBody
    p = InStr(1, html, "<body", vbTextCompare)
    If p > 0 Then p = InStr(p, html, ">")
    If p > 0 Then
        posta.HTMLBody = Left$(html, p) & govde & Mid$(html, p + 1)
    Else
        posta.HTMLBody = govde & html
    End If

    TeklifIslemLoguV2 "Outlook taslağı açıldı", no, "Başarılı", tamYol
    On Error Resume Next
    posta.GetInspector.Activate
    Exit Sub
Hata:
    MsgBox "Outlook PDF işlemi tamamlanamadı." & vbCrLf & "Aşama: " & asama & vbCrLf & _
        "Hata " & Err.Number & ": " & Err.Description, vbExclamation, "Outlook " & SURUM
End Sub

Private Function HtmlKacis(ByVal metin As String) As String
    HtmlKacis = Replace(Replace(Replace(metin, "&", "&amp;"), "<", "&lt;"), ">", "&gt;")
End Function

Public Sub OutlookDugmesiniBaglaV1()
    On Error GoTo Hata
    ThisWorkbook.Worksheets("Outlook E-posta").Shapes("Outlook PDF Düğmesi").OnAction = "'" & ThisWorkbook.Name & "'!TeklifiOutlookPDFTaslakV1"
    MsgBox "Outlook PDF düğmesi hazır.", vbInformation
    Exit Sub
Hata:
    MsgBox "Outlook düğmesi bağlanamadı: " & Err.Description, vbExclamation
End Sub

Public Sub OutlookPDFTestiV1()
    TeklifiOutlookPDFTaslakV1
End Sub

Rem ===== TEKLİF YÖNETİMİ =====

Private Function SiraNo(ByVal no As String, ByVal yilOnEki As String) As Long
    Dim kalan As String
    If Left$(no, Len(yilOnEki)) <> yilOnEki Then Exit Function
    kalan = Mid$(no, Len(yilOnEki) + 1)
    If Len(kalan) = 0 Or Len(kalan) > 9 Or kalan Like "*[!0-9]*" Then Exit Function
    SiraNo = CLng(kalan)
End Function

' Sıra numarası her yıl 1'den başlar; Ayarlar!B6, form ve arşivdeki en büyük numaranın bir fazlası alınır.
Public Function SonrakiTeklifNo() As String
    Dim onEk As String, yilOnEki As String, enBuyuk As Long, n As Long, c As Range, t As ListObject
    onEk = Trim$(CStr(AyarlarSayfasi.Range("B5").Value))
    If Len(onEk) = 0 Then onEk = "TKL"
    yilOnEki = onEk & "-" & Year(Date) & "-"
    ' B6 her zaman başlangıç; boş form / yıl değişiminde sıra kaybını önler.
    enBuyuk = CLng(Sayi(AyarlarSayfasi.Range("B6").Value))
    n = SiraNo(AktifTeklifNo(), yilOnEki)
    If n > enBuyuk Then enBuyuk = n
    Set t = ArsivTablosu
    If Not t.DataBodyRange Is Nothing Then
        For Each c In t.ListColumns("Teklif No").DataBodyRange.Cells
            n = SiraNo(CStr(c.Value), yilOnEki)
            If n > enBuyuk Then enBuyuk = n
        Next c
    End If
    Do
        enBuyuk = enBuyuk + 1
        SonrakiTeklifNo = yilOnEki & Format$(enBuyuk, "0000")
    Loop While TeklifNoArsivdeVarV2(SonrakiTeklifNo)
End Function

Public Sub YeniTeklifV2()
    On Error GoTo Hata
    Dim f As Worksheet, a As Worksheet, no As String, eskiNo As String, cevap As VbMsgBoxResult, mesaj As String
    Set f = TeklifSayfasi
    Set a = AyarlarSayfasi
    eskiNo = AktifTeklifNo()

    If Len(eskiNo) > 0 And Application.WorksheetFunction.CountA(f.Range("A8:A30")) > 0 And Not TeklifNoArsivdeVarV2(eskiNo) Then
        cevap = MsgBox("Mevcut teklif (" & eskiNo & ") henüz arşivlenmedi ve yeni teklifte silinecek." & vbCrLf & vbCrLf & _
            "Evet: Önce arşivle, sonra yeni teklif aç" & vbCrLf & "Hayır: Arşivlemeden yeni teklif aç" & vbCrLf & "İptal: Vazgeç", _
            vbYesNoCancel + vbQuestion, "Yeni Teklif " & SURUM)
        If cevap = vbCancel Then Exit Sub
        If cevap = vbYes Then
            If Not TeklifiArsivleIc(mesaj) Then
                MsgBox "Arşivleme yapılamadı, yeni teklif açılmadı." & vbCrLf & mesaj, vbExclamation
                Exit Sub
            End If
        End If
    End If

    no = SonrakiTeklifNo()
    Application.EnableEvents = False
    f.Unprotect Password:=SIFRE
    a.Range("B6").Value = SiraNo(no, Left$(no, InStrRev(no, "-")))
    f.Range("J3").Value = no
    f.Range("J4").Value = Date
    f.Range("D3:G3").ClearContents
    f.Range("A8:A30,D8:E30").ClearContents
    f.Range("G8:G30").Value = a.Range("B8").Value
    f.Range("I8:I30").Value = "TRY"
    f.Range("A35").Value = "Teklife ilişkin açıklamalarınızı buraya yazınız."
    ThisWorkbook.Worksheets("Teklif Yönetimi").Range("G6").Value = "Taslak"
    SayfayiKoru f
    Application.EnableEvents = True
    TeklifIslemLoguV2 "Yeni teklif", no, "Başarılı", ""
    MsgBox "Yeni teklif hazır: " & no, vbInformation, "Yeni Teklif " & SURUM
    Exit Sub
Hata:
    Dim hataNo As Long, hataAciklama As String
    hataNo = Err.Number: hataAciklama = Err.Description
    On Error Resume Next
    Application.EnableEvents = True
    SayfayiKoru f
    MsgBox "Yeni teklif oluşturulamadı. Hata " & hataNo & ": " & hataAciklama, vbExclamation
End Sub

Private Function TeklifiArsivleIc(ByRef mesaj As String) As Boolean
    On Error GoTo Hata
    Dim f As Worksheet, satir As Range, no As String, durum As String
    Set f = TeklifSayfasi
    no = AktifTeklifNo()
    If Len(no) = 0 Then Err.Raise vbObjectError + 501, , "Teklif numarası boş."
    If Len(Trim$(CStr(f.Range("D3").Value))) = 0 Then Err.Raise vbObjectError + 502, , "Müşteri bilgisi boş."
    If Application.WorksheetFunction.CountA(f.Range("A8:A30")) = 0 Then Err.Raise vbObjectError + 503, , "Teklif kalemi bulunmuyor."
    If TeklifNoArsivdeVarV2(no) Then Err.Raise vbObjectError + 504, , "Bu teklif numarası daha önce arşivlenmiş."
    durum = Trim$(CStr(ThisWorkbook.Worksheets("Teklif Yönetimi").Range("G6").Value))
    If Len(durum) = 0 Then durum = "Taslak"
    Set satir = TabloSatiriAl(ArsivTablosu)
    With satir
        .Cells(1, 1).Value = no
        .Cells(1, 2).Value = f.Range("J4").Value
        .Cells(1, 3).Value = f.Range("D3").Value
        .Cells(1, 4).Value = f.Range("K33").Value
        .Cells(1, 5).Value = durum
        .Cells(1, 6).Value = f.Range("J5").Value
        .Cells(1, 7).Value = Date + 7
        .Cells(1, 8).Value = 0.25
        .Cells(1, 9).Value = SonPDFYolu(no)
        .Cells(1, 10).Value = Application.UserName
        .Cells(1, 11).Value = f.Range("A35").Value
    End With
    TeklifIslemLoguV2 "Teklif arşivlendi", no, "Başarılı", CStr(satir.Cells(1, 9).Value)
    mesaj = "Teklif arşivlendi: " & no
    TeklifiArsivleIc = True
    Exit Function
Hata:
    mesaj = "Hata " & Err.Number & ": " & Err.Description
End Function

Public Sub TeklifiArsivleV2()
    Dim mesaj As String
    If TeklifiArsivleIc(mesaj) Then
        MsgBox mesaj, vbInformation, "Arşiv " & SURUM
    Else
        MsgBox "Teklif arşivlenemedi." & vbCrLf & mesaj, vbExclamation, "Arşiv " & SURUM
    End If
End Sub

Private Function SonPDFYolu(ByVal no As String) As String
    Dim t As ListObject, i As Long
    On Error GoTo Son
    Set t = GunlukTablosu
    If t.DataBodyRange Is Nothing Then Exit Function
    For i = t.ListRows.Count To 1 Step -1
        If CStr(t.DataBodyRange.Cells(i, 3).Value) = no And LCase$(Right$(CStr(t.DataBodyRange.Cells(i, 5).Value), 4)) = ".pdf" Then
            SonPDFYolu = CStr(t.DataBodyRange.Cells(i, 5).Value)
            Exit Function
        End If
    Next i
Son:
End Function

' Fatura değerlerle sabitlenir: sonraki kur güncellemeleri kesilmiş faturanın tutarını değiştirmez.
Public Function FaturayaAktarIc(ByVal kullaniciyaSor As Boolean, ByVal gunlugeYaz As Boolean) As String
    Dim kaynak As Worksheet, hedef As Worksheet, no As String, i As Long, shp As Shape
    Set kaynak = TeklifSayfasi
    Set hedef = ThisWorkbook.Worksheets("Fatura")
    no = AktifTeklifNo()
    If Application.WorksheetFunction.CountA(kaynak.Range("A8:A30")) = 0 Then Err.Raise vbObjectError + 601, , "Teklifte ürün kalemi yok."
    If kullaniciyaSor And Len(Trim$(CStr(hedef.Range("J3").Value))) > 0 Then
        If MsgBox("Fatura sayfasındaki mevcut taslak (" & hedef.Range("J3").Value & ") silinip yerine " & no & " aktarılacak. Devam edilsin mi?", _
            vbYesNo + vbQuestion, "Faturaya Aktar " & SURUM) <> vbYes Then Exit Function
    End If

    Application.ScreenUpdating = False
    On Error GoTo Temizle
    hedef.Unprotect Password:=SIFRE
    For i = hedef.Shapes.Count To 1 Step -1
        Set shp = hedef.Shapes(i)
        If Not Intersect(shp.TopLeftCell, hedef.Range("A1:K40")) Is Nothing Then shp.Delete
    Next i
    hedef.Range("A1:K40").UnMerge
    hedef.Range("A1:K40").Clear
    kaynak.Range("A1:K40").Copy
    hedef.Range("A1").PasteSpecial xlPasteAll
    Application.CutCopyMode = False
    hedef.Range("A1:K40").Value = hedef.Range("A1:K40").Value
    On Error Resume Next
    hedef.Range("A1:K40").Validation.Delete
    On Error GoTo Temizle
    GorselleriKopyala kaynak, hedef
    hedef.Range("B1").Value = "FATURA TASLAĞI"
    hedef.Range("H3").Value = "Fatura No"
    hedef.Range("J3").Value = Replace(no, AyarlarSayfasi.Range("B5").Value & "-", "FTR-", , 1)
    hedef.Range("J4").Value = Date
    hedef.Range("A6").Value = "FATURA KALEMLERİ"
    SayfaDuzeniAyarla hedef
    Application.ScreenUpdating = True
    If gunlugeYaz Then TeklifIslemLoguV2 "Faturaya aktarıldı", no, "Başarılı", ""
    FaturayaAktarIc = CStr(hedef.Range("J3").Value)
    Exit Function
Temizle:
    Application.CutCopyMode = False
    Application.ScreenUpdating = True
    Err.Raise Err.Number, Err.Source, Err.Description
End Function

' PasteSpecial şekilleri taşımaz; logo ve kaşe/imza görselleri ayrıca aynı konuma kopyalanır.
Private Sub GorselleriKopyala(ByVal kaynak As Worksheet, ByVal hedef As Worksheet)
    Dim shp As Shape, yeni As Shape
    For Each shp In kaynak.Shapes
        If shp.Type = msoPicture Then
            If Not Intersect(shp.TopLeftCell, kaynak.Range("A1:K40")) Is Nothing Then
                shp.Copy
                hedef.Paste Destination:=hedef.Range(shp.TopLeftCell.Address)
                Set yeni = hedef.Shapes(hedef.Shapes.Count)
                yeni.Width = shp.Width
                yeni.Height = shp.Height
                yeni.Left = hedef.Range(shp.TopLeftCell.Address).Left + (shp.Left - shp.TopLeftCell.Left)
                yeni.Top = hedef.Range(shp.TopLeftCell.Address).Top + (shp.Top - shp.TopLeftCell.Top)
                yeni.Name = shp.Name
            End If
        End If
    Next shp
    Application.CutCopyMode = False
End Sub

Public Sub TeklifiFaturayaAktarV2()
    On Error GoTo Hata
    Dim faturaNo As String
    faturaNo = FaturayaAktarIc(True, True)
    If Len(faturaNo) = 0 Then Exit Sub
    ThisWorkbook.Worksheets("Fatura").Activate
    MsgBox "Teklif fatura taslağına aktarıldı: " & faturaNo & vbCrLf & "Tutarlar sabitlendi; kur değişiklikleri faturayı etkilemez.", vbInformation, "Fatura " & SURUM
    Exit Sub
Hata:
    Application.CutCopyMode = False
    Application.ScreenUpdating = True
    MsgBox "Faturaya aktarma başarısız. Hata " & Err.Number & ": " & Err.Description, vbExclamation
End Sub

Private Sub DugmeleriBagla()
    Dim ws As Worksheet, kitap As String, durum As String
    Set ws = ThisWorkbook.Worksheets("Teklif Yönetimi")
    kitap = "'" & ThisWorkbook.Name & "'!"
    DugmeyiBagla ws, "Yeni Teklif Düğmesi", kitap & "YeniTeklifV2"
    DugmeyiBagla ws, "Arşivle Düğmesi", kitap & "TeklifiArsivleV2"
    DugmeyiBagla ws, "Faturaya Aktar Düğmesi", kitap & "TeklifiFaturayaAktarV2"
    DugmeyiBagla ws, "Yönetim PDF Düğmesi", kitap & "TeklifiMasaustunePDFKaydetV2"
    DugmeyiBagla ws, "Yönetim Outlook Düğmesi", kitap & "TeklifiOutlookPDFTaslakV1"
    DugmeyiBagla ws, "Formülleri Koru Düğmesi", kitap & "TeklifSayfasiniKoruV2"
    durum = "Sistem kontrolü: 6 yönetim düğmesi bağlı ve hazır (" & SURUM & ")"
    If CStr(ws.Range("A8").Value) <> durum Then ws.Range("A8").Value = durum
End Sub

' Yalnızca farklıysa yazılır; aksi halde her açılışta kitap "değişti" sayılırdı.
Private Sub DugmeyiBagla(ByVal ws As Worksheet, ByVal dugme As String, ByVal makro As String)
    With ws.Shapes(dugme)
        If .OnAction <> makro Then .OnAction = makro
    End With
End Sub

Public Sub YonetimDugmeleriniBaglaV2()
    On Error GoTo Hata
    DugmeleriBagla
    Exit Sub
Hata:
    MsgBox "Yönetim düğmeleri bağlanamadı: " & Err.Description, vbExclamation
End Sub

Public Sub AcilisIslemleri()
    On Error Resume Next
    SayfayiKoru TeklifSayfasi
    DugmeleriBagla
    Application.OnTime Now + TimeSerial(0, 0, 2), "'" & ThisWorkbook.Name & "'!KurlariSessizGuncelle"
End Sub

Public Sub TeklifSayfasiniKoruV2()
    On Error GoTo Hata
    Dim ws As Worksheet
    Set ws = TeklifSayfasi
    ws.Unprotect Password:=SIFRE
    ws.Cells.Locked = True
    ws.Range("D3:G3,J3:K4,A8:A30,D8:E30,G8:G30,I8:I30,A35:F39").Locked = False
    SayfayiKoru ws
    ws.EnableSelection = xlUnlockedCells
    MsgBox "Formüller korundu; giriş hücreleri açık.", vbInformation
    Exit Sub
Hata:
    MsgBox "Sayfa koruması uygulanamadı: " & Err.Description, vbExclamation
End Sub

Public Sub TeklifSayfasiKorumasiniKaldirV2()
    On Error GoTo Hata
    TeklifSayfasi.Unprotect Password:=SIFRE
    MsgBox "Teklif Formu koruması kaldırıldı.", vbInformation
    Exit Sub
Hata:
    MsgBox "Koruma kaldırılamadı: " & Err.Description, vbExclamation
End Sub

' Günlük yazılamasa bile çağıran işlem (PDF, arşiv vb.) başarısız sayılmaz.
Public Sub TeklifIslemLoguV2(ByVal islem As String, ByVal teklifNo As String, ByVal sonuc As String, ByVal dosyaYolu As String)
    Dim satir As Range
    On Error GoTo Son
    Set satir = TabloSatiriAl(GunlukTablosu)
    With satir
        .Cells(1, 1).Value = Now
        .Cells(1, 2).Value = islem
        .Cells(1, 3).Value = teklifNo
        .Cells(1, 4).Value = TeklifSayfasi.Range("D3").Value
        .Cells(1, 5).Value = dosyaYolu
        .Cells(1, 6).Value = sonuc
    End With
Son:
End Sub

Private Function TeklifNoArsivdeVarV2(ByVal no As String) As Boolean
    Dim t As ListObject
    Set t = ArsivTablosu
    If t.DataBodyRange Is Nothing Then Exit Function
    TeklifNoArsivdeVarV2 = (Application.WorksheetFunction.CountIf(t.ListColumns("Teklif No").DataBodyRange, no) > 0)
End Function

Rem ===== TEST =====

Public Sub TumFonksiyonlariTestEtV5()
    Dim sonuc As String
    sonuc = SistemTestiSonucu(True)
    MsgBox sonuc, IIf(Left$(sonuc, 5) = "BAŞAR", vbInformation, vbExclamation), "Sistem Testi " & SURUM
End Sub

' Kullanıcı verisini değiştirmeden çalışır: test edilen hücreler sonunda eski haline getirilir.
Public Function SistemTestiSonucu(Optional ByVal panoTesti As Boolean = True) As String
    On Error GoTo Hata
    Dim ws As Worksheet, pdf As String, adim As String, rapor As String
    Dim eski As Variant, girisler As Variant, i As Long
    Set ws = TeklifSayfasi
    SayfayiKoru ws
    ' Yalnızca giriş hücreleri değiştirilir; B, C, F, H, J, K sütunlarındaki formüllere dokunulmaz.
    girisler = Array("A30", "D30", "E30", "G30", "I30")
    ReDim eski(0 To 4)
    For i = 0 To 4
        eski(i) = ws.Range(girisler(i)).Value
    Next i

    adim = "Telefon temizleme"
    If TemizTelefonV5("0532 123 45 67") <> "905321234567" Then Err.Raise vbObjectError + 210, , "0532... -> " & TemizTelefonV5("0532 123 45 67")
    If TemizTelefonV5("+90 (532) 123 45 67") <> "905321234567" Then Err.Raise vbObjectError + 210, , "+90... hatalı"
    If TemizTelefonV5("0090 532 123 45 67") <> "905321234567" Then Err.Raise vbObjectError + 210, , "0090... hatalı"
    rapor = rapor & "• Telefon temizleme" & vbCrLf

    adim = "Dosya adı"
    If DosyaAdiTemizle("T:1/2?*") <> "T-1-2--" Then Err.Raise vbObjectError + 211, , DosyaAdiTemizle("T:1/2?*")
    If DosyaAdiTemizle("  ") <> "Numarasiz" Then Err.Raise vbObjectError + 211, , "boş ad"
    rapor = rapor & "• Dosya adı temizleme" & vbCrLf

    adim = "Kur sayı çevirme"
    If Abs(YerelSayiyaCevirV3("49,9679") - 49.9679) > 0.00001 Then Err.Raise vbObjectError + 216, , "49,9679"
    If Abs(YerelSayiyaCevirV3("1.234,56") - 1234.56) > 0.00001 Then Err.Raise vbObjectError + 216, , "1.234,56"
    If Abs(YerelSayiyaCevirV3("49.9679") - 49.9679) > 0.00001 Then Err.Raise vbObjectError + 216, , "49.9679"
    If Abs(SatisKuruBulV6("<tr><td>USD</td><td>48,7859</td><td>49,9679</td></tr>", "USD") - 49.9679) > 0.00001 Then Err.Raise vbObjectError + 217, , "HTML kur ayrıştırma"
    rapor = rapor & "• Kur ayrıştırma ve sayı çevirme" & vbCrLf

    adim = "Ürün formülleri"
    Application.EnableEvents = False
    ws.Range("A30,D30,E30,I30").ClearContents
    ws.Range("A30").Value = "Y.S. HELYUM GAZI"
    ws.Range("D30").Value = 1
    ws.Range("E30").Value = 10
    ws.Range("I30").Value = "USD"
    Application.EnableEvents = True
    Hesapla
    If Sayi(ws.Range("B30").Value) <> 200 Then Err.Raise vbObjectError + 212, , "BAR formülü: " & ws.Range("B30").Value
    If Abs(Sayi(ws.Range("C30").Value) - 9.2) > 0.0001 Then Err.Raise vbObjectError + 213, , "Gaz miktarı formülü"
    If Sayi(ws.Range("J30").Value) <= 1 Then Err.Raise vbObjectError + 214, , "Kur formülü"
    rapor = rapor & "• Ürün, gaz ve kur formülleri" & vbCrLf

    adim = "Ürün varsayılanları"
    Application.EnableEvents = False
    ws.Range("A30,D30,E30,I30").ClearContents
    Application.EnableEvents = True
    ws.Range("A30").Value = "Y.S. ARGON GAZI"
    If Sayi(ws.Range("E30").Value) <> 1000 Or CStr(ws.Range("I30").Value) <> "TRY" Or Sayi(ws.Range("D30").Value) <> 1 Then _
        Err.Raise vbObjectError + 218, , "Otomatik doldurma: E=" & ws.Range("E30").Value & " I=" & ws.Range("I30").Value & " D=" & ws.Range("D30").Value
    rapor = rapor & "• Ürün seçilince fiyat/para birimi/KDV doldurma" & vbCrLf

    GirisleriGeriYukle ws, girisler, eski
    Hesapla
    If Not ws.Range("B30").HasFormula Or Not ws.Range("F30").HasFormula Then Err.Raise vbObjectError + 220, , "Test sonrası formüller bozuldu"

    adim = "Teklif numarası"
    If Len(SonrakiTeklifNo()) = 0 Then Err.Raise vbObjectError + 219, , "Numara üretilemedi"
    rapor = rapor & "• Sonraki teklif no: " & SonrakiTeklifNo() & vbCrLf

    adim = "PDF oluşturma"
    pdf = TeklifPDFOlustur(Environ$("TEMP"), "Teklif_PDF_Test")
    rapor = rapor & "• A4 PDF oluşturma" & vbCrLf
    If panoTesti Then
        adim = "Pano"
        DosyayiPanoyaKopyalaV5 pdf
        rapor = rapor & "• PDF'yi Windows panosuna alma" & vbCrLf
    End If
    Kill pdf

    adim = "Gönderim kontrolü"
    rapor = rapor & "• Gönderim kontrolü: " & IIf(Len(GonderimUyarilari()) = 0, "uyarı yok", Replace(GonderimUyarilari(), vbCrLf, " ")) & vbCrLf

    SistemTestiSonucu = "BAŞARILI - tüm testler geçti:" & vbCrLf & rapor & "Sürüm: " & SURUM
    Exit Function
Hata:
    Dim hataMetni As String
    hataMetni = "Hata " & Err.Number & ": " & Err.Description
    On Error Resume Next
    If IsArray(eski) Then GirisleriGeriYukle ws, girisler, eski
    Application.EnableEvents = True
    SistemTestiSonucu = "BAŞARISIZ - adım: " & adim & vbCrLf & hataMetni & vbCrLf & "Geçen testler:" & vbCrLf & rapor
End Function

Private Sub GirisleriGeriYukle(ByVal ws As Worksheet, ByVal girisler As Variant, ByVal eski As Variant)
    Dim i As Long
    Application.EnableEvents = False
    For i = LBound(girisler) To UBound(girisler)
        ws.Range(girisler(i)).Value = eski(i)
    Next i
    Application.EnableEvents = True
End Sub

' Lisansı dolmuş/kısıtlı Excel'de bu iki komut 1004 verir; işlem yarıda kalmasın diye hata yutulur.
Private Sub DurumCubugu(ByVal metin As Variant)
    On Error Resume Next
    Application.StatusBar = metin
End Sub

Private Sub Hesapla()
    On Error Resume Next
    Application.Calculate
End Sub

' Kısıtlı (lisanssız) Excel'de korumalı sayfaya tekrar Protect 1004 verir; önce kaldırılıp yeniden uygulanır.
Private Sub SayfayiKoru(ByVal ws As Worksheet)
    On Error Resume Next
    ws.Unprotect Password:=SIFRE
    ws.Protect Password:=SIFRE, UserInterfaceOnly:=True, AllowFiltering:=True
End Sub
