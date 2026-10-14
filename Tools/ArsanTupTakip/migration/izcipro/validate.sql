-- Aktarım sonrası kaynak (izcipro) ile hedef (tuptakip) karşılaştırması. Yalnızca okur.
\pset footer off
\echo '== Kayıt sayıları (kaynak / hedef)'
SELECT 'müşteri' AS kayit, (SELECT count(*) FROM izcipro.tbl_carikart) AS kaynak, (SELECT count(*) FROM tuptakip.customers) AS hedef
UNION ALL SELECT 'tüp (envanter kartı)', (SELECT count(*) FROM izcipro.tbl_envanterkart), (SELECT count(*) FROM tuptakip.cylinders WHERE legacysource = 'İzciPro')
UNION ALL SELECT 'tüp (silinmiş kart, inceleme)', (SELECT count(*) FROM izcipro.tbl_tupenvanterkimlikkart k WHERE NOT EXISTS (SELECT 1 FROM izcipro.tbl_envanterkart e WHERE e.id = k.tbl_envanterkartsec)),
                                              (SELECT count(*) FROM tuptakip.cylinders WHERE legacysource LIKE 'İzciPro (silinmiş%')
UNION ALL SELECT 'hareket', (SELECT count(*) FROM izcipro.tbl_tupenvanteryasamdongusu), (SELECT count(*) FROM tuptakip.cylindermovements WHERE legacyid IS NOT NULL)
UNION ALL SELECT 'araç', (SELECT count(*) FROM izcipro.tbl_depo WHERE depoadi ~ '^\d{2}\s'), (SELECT count(*) FROM tuptakip.vehicles);

\echo '== Son durum: kaynaktaki son hareket ile hedefteki tüp durumu aynı mı'
WITH son AS (
    SELECT DISTINCT ON (y.tbl_envanterkartsec) y.tbl_envanterkartsec AS cid, y.tbl_deposec_sondurum AS depo, y.durumdolu, y.tbl_carikartsec_teslimalan AS alan, y.harekettarihi
    FROM izcipro.tbl_tupenvanteryasamdongusu y ORDER BY y.tbl_envanterkartsec, y.harekettarihi DESC, y.id DESC)
SELECT count(*) AS karsilastirilan,
       count(*) FILTER (WHERE c.isfull IS DISTINCT FROM s.durumdolu) AS dolu_bos_farkli,
       count(*) FILTER (WHERE s.depo = 3 AND c.status = 3 AND c.currentcustomerid IS DISTINCT FROM s.alan) AS musteri_farkli,
       count(*) FILTER (WHERE c.lastmovementat IS DISTINCT FROM s.harekettarihi) AS tarih_farkli,
       count(*) FILTER (WHERE (s.depo = 3) <> (c.status IN (3, 4))) AS konum_farkli
FROM son s JOIN tuptakip.cylinders c ON c.id = s.cid;

\echo '== Durum dağılımı (hedef)'
SELECT status, CASE status WHEN 1 THEN 'Depoda' WHEN 2 THEN 'Araçta' WHEN 3 THEN 'Müşteride' WHEN 4 THEN 'Dolumda' WHEN 5 THEN 'Serviste' ELSE 'Hurda' END AS durum,
       count(*) AS toplam, count(*) FILTER (WHERE isfull) AS dolu, count(*) FILTER (WHERE NOT isfull) AS bos,
       count(*) FILTER (WHERE isactive) AS aktif, count(*) FILTER (WHERE needsreview) AS inceleme
FROM tuptakip.cylinders GROUP BY status ORDER BY status;

\echo '== Araçlardaki tüpler'
SELECT vehicleplate, count(*) FROM tuptakip.cylinders WHERE status = 2 GROUP BY vehicleplate ORDER BY 1;

\echo '== Gaz türleri ve kapasiteler'
SELECT gastype, capacity, capacityunit, count(*) FROM tuptakip.cylinders GROUP BY 1, 2, 3 ORDER BY 4 DESC LIMIT 40;
SELECT count(*) FILTER (WHERE capacity = 0) AS kapasite_yok, count(*) FILTER (WHERE testduedate IS NULL) AS test_tarihi_yok,
       count(*) FILTER (WHERE manufacturerserial IS NULL) AS uretici_seri_yok, count(*) FILTER (WHERE ownercustomerid IS NOT NULL) AS musteri_mali,
       count(*) FILTER (WHERE needsreview) AS inceleme, count(*) FILTER (WHERE NOT isactive) AS pasif
FROM tuptakip.cylinders;

\echo '== Müşteriler'
SELECT count(*) AS toplam, count(*) FILTER (WHERE isactive) AS aktif, count(*) FILTER (WHERE phone IS NOT NULL) AS telefon,
       count(*) FILTER (WHERE city IS NOT NULL) AS il, count(*) FILTER (WHERE taxnumber IS NOT NULL) AS vergi_no,
       count(DISTINCT code) AS farkli_kod
FROM tuptakip.customers;
SELECT city, count(*) FROM tuptakip.customers GROUP BY 1 ORDER BY 2 DESC LIMIT 8;

\echo '== Örnek etiket: 51032186920 / 593114'
SELECT c.id, c.serialnumber, c.barcode, c.manufacturerserial, c.gastype, c.capacity, c.status, c.isfull, c.testduedate::date, c.lasttestdate::date,
       (SELECT count(*) FROM tuptakip.cylindermovements m WHERE m.cylinderid = c.id) AS hareket
FROM tuptakip.cylinders c WHERE c.serialnumber = '51032186920';
