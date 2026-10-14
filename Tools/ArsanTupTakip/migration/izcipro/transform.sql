-- İzciPro arşivinden (izcipro şeması) ArsanTüpTakip tablolarına aktarım. Tek işlem: hata olursa hiçbir şey yazılmaz.
-- tuptakip şeması boş olmalıdır (deneme verisini silmek için önce reset.sql çalıştırılır).
-- Eski kayıt numaraları korunur: müşteri id = tbl_CariKart.ID, tüp id = tbl_EnvanterKart.ID (silinmiş kartlarda
-- yaşam döngüsündeki tüp numarası), hareket legacyid = tbl_TupEnvanterYasamDongusu.ID.
\set ON_ERROR_STOP on
BEGIN;

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM tuptakip.cylinders) OR EXISTS (SELECT 1 FROM tuptakip.customers)
       OR EXISTS (SELECT 1 FROM tuptakip.cylindermovements) OR EXISTS (SELECT 1 FROM tuptakip.vehicles) THEN
        RAISE EXCEPTION 'tuptakip şemasında kayıt var; aktarım yalnızca boş sisteme yapılır (önce reset.sql).';
    END IF;
END $$;

CREATE FUNCTION pg_temp.sane_date(value timestamp) RETURNS timestamp LANGUAGE sql IMMUTABLE AS
$$ SELECT CASE WHEN value >= '1950-01-01' AND value < '2041-01-01' THEN value END $$;
CREATE FUNCTION pg_temp.capacity_text(value text) RETURNS numeric LANGUAGE sql IMMUTABLE AS
$$ SELECT NULLIF(replace((regexp_match(COALESCE(value, ''), '(\d+(?:[.,]\d+)?)'))[1], ',', '.')::numeric, 0) $$;

-- Cari #1: eski sistemde depo hareketlerinde "teslim eden/alan" olarak kullanılan şirketin kendisi.
CREATE TEMP TABLE own_company AS SELECT 1 AS id;

------------------------------------------------------------------------------------------------ müşteriler
INSERT INTO tuptakip.customers (id, code, companyname, contactname, phone, phone2, email, city, district, address, taxoffice, taxnumber,
                                isactive, createdat, customertype, note, legacyid, legacysource)
SELECT c.id,
       COALESCE(NULLIF(btrim(c.carikod), ''), 'IZ-' || c.id),
       COALESCE(NULLIF(btrim(c.cariunvan), ''), NULLIF(btrim(c.caribaslik), ''), 'Cari #' || c.id),
       NULLIF(btrim(c.yetkilikisi), ''),
       NULLIF(btrim(c.telefonfaxgsm), ''),
       NULLIF(concat_ws(' / ', NULLIF(btrim(c.telefonfaxgsm2), ''), NULLIF(btrim(c.telefonfaxgsm3), '')), ''),
       NULLIF(btrim(c.eposta), ''),
       COALESCE(NULLIF(btrim(c.yer_il), ''), CASE WHEN y.ustyerid IS NULL THEN y.yeradi ELSE p.yeradi END),
       COALESCE(NULLIF(btrim(c.yer_ilce), ''), CASE WHEN y.ustyerid IS NOT NULL THEN y.yeradi END),
       NULLIF(concat_ws(' ', NULLIF(btrim(c.adressatir1), ''), NULLIF(btrim(c.adressatir2), '')), ''),
       NULLIF(btrim(c.vergidairesi), ''),
       NULLIF(btrim(c.vergikimlikno), ''),
       COALESCE(c.carikartaktif, true) AND c.id NOT IN (SELECT id FROM own_company),
       COALESCE(c.islemtarihi, localtimestamp),
       t.caritipadi,
       NULLIF(concat_ws(' · ',
           NULLIF(btrim(c.aciklama), ''),
           CASE WHEN c.id IN (SELECT id FROM own_company) THEN 'İzciPro firma kartı (şirketin kendisi); müşteri olarak kullanılmaz' END,
           CASE WHEN COALESCE(c.bakiye, 0) <> 0 THEN 'İzciPro bakiye: ' || c.bakiye END), ''),
       c.id,
       'İzciPro'
FROM izcipro.tbl_carikart c
LEFT JOIN izcipro.ililce y ON y.id = c.yerid
LEFT JOIN izcipro.ililce p ON p.id = y.ustyerid
LEFT JOIN izcipro.fix_caritip t ON t.id = c.fix_caritipsec;

------------------------------------------------------------------------------------------------ depolar / araçlar
-- Plaka adlı depolar araçtır; diğerleri depo (dolum tesisi, sayım). "Müşteri deposu" = müşteride.
CREATE TEMP TABLE depot_map AS
SELECT d.id, d.depoadi,
       CASE WHEN upper(btrim(d.depoadi)) ~ '^\d{2}\s*[A-ZÇĞİÖŞÜ]{1,3}\s*\d{2,4}$'
            THEN regexp_replace(upper(btrim(d.depoadi)), '^(\d{2})\s*([A-ZÇĞİÖŞÜ]{1,3})\s*(\d{2,4})$', '\1 \2 \3') END AS plate
FROM izcipro.tbl_depo d;

INSERT INTO tuptakip.vehicles (plate, name, drivername, note, isactive, createdat)
SELECT m.plate, 'İzciPro deposu: ' || m.depoadi,
       (SELECT string_agg(NULLIF(btrim(concat_ws(' ', p.personelad, p.personelsoyad)), ''), ', ')
          FROM izcipro.tbl_personel p WHERE p.deposec = m.id AND COALESCE(p.aktif, true)),
       'İzciPro depo #' || m.id, true, localtimestamp
FROM depot_map m WHERE m.plate IS NOT NULL;

------------------------------------------------------------------------------------------------ hareketler (ara tablo)
CREATE TEMP TABLE yd AS
SELECT b.*,
       COALESCE(lag(b.to_status) OVER (PARTITION BY b.cid ORDER BY b.at, b.id), 1) AS from_status,
       row_number() OVER (PARTITION BY b.cid ORDER BY b.at DESC, b.id DESC) AS rn
FROM (
    SELECT y.id,
           y.tbl_envanterkartsec AS cid,
           y.harekettarihi AS at,
           y.fix_envanterharekettipsec AS tip,
           y.tbl_deposec_sondurum AS depo,
           y.durumdolu AS dolu,
           y.tbl_carikartsec_teslimeden AS eden,
           y.tbl_carikartsec_teslimalan AS alan,
           y.tbl_kategorisec AS kategori,
           y.dolumnumarasi, y.partinumarasi, y.aciklama,
           y.tbl_personelsec AS personel,
           CASE
               WHEN y.tbl_deposec_sondurum = 3 AND ca.fix_caritipsec = 6 THEN 4                 -- dolum tesisi carisinde
               WHEN y.tbl_deposec_sondurum = 3 THEN 3                                           -- müşteride
               WHEN dm.plate IS NOT NULL OR y.tbl_deposec_sondurum = 6 THEN 2                    -- araçta (depo 6: tanımsız personel deposu)
               ELSE 1                                                                           -- depoda
           END AS to_status,
           dm.plate,
           ca.id AS alan_exists,
           ce.id AS eden_exists
    FROM izcipro.tbl_tupenvanteryasamdongusu y
    LEFT JOIN depot_map dm ON dm.id = y.tbl_deposec_sondurum
    LEFT JOIN izcipro.tbl_carikart ca ON ca.id = y.tbl_carikartsec_teslimalan
    LEFT JOIN izcipro.tbl_carikart ce ON ce.id = y.tbl_carikartsec_teslimeden
) b;
CREATE INDEX ON yd (cid) WHERE rn = 1;

CREATE TEMP TABLE last_state AS SELECT * FROM yd WHERE rn = 1;
CREATE UNIQUE INDEX ON last_state (cid);

------------------------------------------------------------------------------------------------ tüp tipi (stok kartı) çözümleme
CREATE TEMP TABLE stock_map AS
SELECT s.id, btrim(s.stokkartadi) AS product, k.kategoriadi,
       regexp_match(btrim(s.stokkartadi), '^(.*?)\s*\((\d+(?:[.,]\d+)?)\s*KG\)') AS kg,
       regexp_match(btrim(s.stokkartadi), '^(.*?)\s*(\d+(?:[.,]\d+)?)\s*(?:LT|L)\y') AS lt,
       regexp_match(btrim(s.stokkartadi), '^(.*?)\s+(\d+(?:[.,]\d+)?)\s+\d+\s*BAR') AS bar
FROM izcipro.tbl_stokkart s
LEFT JOIN izcipro.tbl_kategori k ON k.id = s.tbl_kategorisec;

ALTER TABLE stock_map ADD COLUMN gas text, ADD COLUMN capacity numeric, ADD COLUMN unit text;
UPDATE stock_map SET
    gas = btrim(regexp_replace(COALESCE(kg[1], lt[1], bar[1], product), '\s+GAZI?$', '')),
    capacity = NULLIF(replace(COALESCE(kg[2], lt[2], bar[2]), ',', '.')::numeric, 0),
    unit = CASE WHEN kg IS NOT NULL THEN 'kg' ELSE 'Litre' END;
UPDATE stock_map SET gas = COALESCE(NULLIF(gas, ''), kategoriadi, product);

------------------------------------------------------------------------------------------------ tüpler
INSERT INTO tuptakip.cylinders (id, serialnumber, barcode, gastype, capacity, capacityunit, status, isfull, vehicleplate, currentcustomerid,
                                lastmovementat, testduedate, isactive, createdat, ownercustomerid, manufacturerserial, productname,
                                manufacturedate, lasttestdate, legacyid, legacysource, needsreview, reviewnote)
SELECT e.id,
       btrim(e.envanterkodu),
       lower(e.envanterguid::text),
       COALESCE(sm.gas, k.kategoriadi, 'Belirsiz'),
       COALESCE(sm.capacity, pg_temp.capacity_text(ki.tuphacmi), 0),
       CASE WHEN sm.capacity IS NOT NULL THEN sm.unit ELSE 'Litre' END,
       COALESCE(ls.to_status, 1),
       ls.dolu,
       CASE WHEN ls.to_status = 2 THEN ls.plate END,
       CASE WHEN ls.to_status = 3 THEN ls.alan_exists END,
       ls.at,
       CASE WHEN ki.gelecektesttarihi >= '2000-01-01' AND ki.gelecektesttarihi < '2041-01-01' THEN date_trunc('day', ki.gelecektesttarihi) END,
       COALESCE(e.envanteraktif, true),
       COALESCE(e.envanterekletarih, localtimestamp),
       CASE WHEN e.tbl_carikartsec NOT IN (0, 1) AND EXISTS (SELECT 1 FROM tuptakip.customers c WHERE c.id = e.tbl_carikartsec) THEN e.tbl_carikartsec END,
       NULLIF(btrim(e.envanterserino), ''),
       sm.product,
       pg_temp.sane_date(ki.imalattarihi),
       pg_temp.sane_date(ki.sontesttarihi),
       e.id,
       'İzciPro',
       ls.cid IS NULL OR (ls.to_status = 3 AND ls.alan_exists IS NULL),
       NULLIF(concat_ws(' · ',
           CASE WHEN ls.cid IS NULL THEN 'Eski sistemde hiç hareket kaydı yok; yeri bilinmiyor (depoda varsayıldı)' END,
           CASE WHEN ls.to_status = 3 AND ls.alan_exists IS NULL THEN 'Müşteride görünüyor ama müşteri kartı eski sistemde silinmiş (cari #' || ls.alan || ')' END,
           CASE WHEN sm.capacity IS NULL AND pg_temp.capacity_text(ki.tuphacmi) IS NULL THEN 'Kapasite bilinmiyor' END,
           CASE WHEN e.tbl_carikartsec NOT IN (0, 1) AND NOT EXISTS (SELECT 1 FROM tuptakip.customers c WHERE c.id = e.tbl_carikartsec)
                THEN 'Sahibi olan cari #' || e.tbl_carikartsec || ' bulunamadı' END), '')
FROM izcipro.tbl_envanterkart e
LEFT JOIN stock_map sm ON sm.id = e.tbl_stokkartsec
LEFT JOIN izcipro.tbl_kategori k ON k.id = e.tbl_kategorisec
LEFT JOIN izcipro.tbl_tupenvanterkimlikkart ki ON ki.tbl_envanterkartsec = e.id
LEFT JOIN last_state ls ON ls.cid = e.id;

-- Envanter kartı silinmiş ama kimlik kartı ve geçmişi olan tüpler: pasif + inceleme bekliyor (QR kimlikleri yok).
INSERT INTO tuptakip.cylinders (id, serialnumber, barcode, gastype, capacity, capacityunit, status, isfull, vehicleplate, currentcustomerid,
                                lastmovementat, testduedate, isactive, createdat, manufacturedate, lasttestdate, legacyid, legacysource,
                                needsreview, reviewnote)
SELECT ki.tbl_envanterkartsec,
       ki.serial,
       ki.serial,
       COALESCE(k.kategoriadi, 'Belirsiz'),
       COALESCE(pg_temp.capacity_text(ki.tuphacmi), 0),
       'Litre',
       COALESCE(ls.to_status, 1),
       ls.dolu,
       CASE WHEN ls.to_status = 2 THEN ls.plate END,
       CASE WHEN ls.to_status = 3 THEN ls.alan_exists END,
       ls.at,
       CASE WHEN ki.gelecektesttarihi >= '2000-01-01' AND ki.gelecektesttarihi < '2041-01-01' THEN date_trunc('day', ki.gelecektesttarihi) END,
       false,
       localtimestamp,
       pg_temp.sane_date(ki.imalattarihi),
       pg_temp.sane_date(ki.sontesttarihi),
       ki.tbl_envanterkartsec,
       'İzciPro (silinmiş envanter kartı)',
       true,
       concat_ws(' · ',
           'İzciPro envanter kartı silinmiş; kimlik kartı ve hareket geçmişi var. QR kimliği yok, etiketi okutulursa bulunamaz',
           'Kimlik kodu: ' || ki.code,
           CASE WHEN ki.serial <> ki.code THEN 'Aynı kimlik kodu başka kayıtta da var' END,
           CASE WHEN pg_temp.capacity_text(ki.tuphacmi) IS NULL THEN 'Kapasite bilinmiyor' END,
           CASE WHEN ls.at IS NOT NULL THEN 'Son hareket: ' || to_char(ls.at, 'DD.MM.YYYY') END)
FROM (
    SELECT x.*, btrim(x.envanterkartkodu) AS code,
           CASE WHEN count(*) OVER (PARTITION BY btrim(x.envanterkartkodu)) > 1
                  OR EXISTS (SELECT 1 FROM tuptakip.cylinders c WHERE c.serialnumber = btrim(x.envanterkartkodu) OR c.barcode = btrim(x.envanterkartkodu))
                THEN btrim(x.envanterkartkodu) || '-IZ' || x.tbl_envanterkartsec
                ELSE btrim(x.envanterkartkodu) END AS serial
    FROM izcipro.tbl_tupenvanterkimlikkart x
    WHERE NOT EXISTS (SELECT 1 FROM izcipro.tbl_envanterkart e WHERE e.id = x.tbl_envanterkartsec)
) ki
LEFT JOIN last_state ls ON ls.cid = ki.tbl_envanterkartsec
LEFT JOIN izcipro.tbl_kategori k ON k.id = ls.kategori;

------------------------------------------------------------------------------------------------ hareket geçmişi
INSERT INTO tuptakip.cylindermovements (cylinderid, customerid, fromstatus, tostatus, movementtype, vehicleplate, isfull, documentnumber,
                                        description, movementdate, performedby, legacyid)
SELECT y.cid,
       CASE WHEN y.to_status IN (3, 4) THEN y.alan_exists
            WHEN y.eden_exists IS NOT NULL AND y.eden NOT IN (SELECT id FROM own_company) THEN y.eden_exists END,
       y.from_status,
       y.to_status,
       COALESCE(t.envanterhareketadi, 'Hareket tipi ' || y.tip),
       CASE WHEN y.to_status = 2 THEN y.plate END,
       y.dolu,
       NULLIF(btrim(y.dolumnumarasi), ''),
       NULLIF(concat_ws(' · ',
           NULLIF(btrim(y.aciklama), ''),
           CASE WHEN NULLIF(btrim(y.partinumarasi), '') IS NOT NULL THEN 'Parti: ' || btrim(y.partinumarasi) END,
           CASE WHEN y.depo = 6 THEN 'İzciPro depo #6 (tanımsız)' END,
           CASE WHEN y.depo = 2 THEN 'İzciPro sayım deposu' END,
           CASE WHEN y.to_status = 3 AND y.alan_exists IS NULL THEN 'Müşteri kartı silinmiş (cari #' || y.alan || ')' END,
           CASE WHEN y.to_status = 4 THEN 'Dolum tesisi: ' || ca.cariunvan END), ''),
       y.at,
       COALESCE(NULLIF(btrim(concat_ws(' ', p.personelad, p.personelsoyad)), ''), 'Bilinmiyor') || ' (İzciPro)',
       y.id
FROM yd y
LEFT JOIN izcipro.fix_envanterharekettip t ON t.id = y.tip
LEFT JOIN izcipro.tbl_personel p ON p.id = y.personel
LEFT JOIN izcipro.tbl_carikart ca ON ca.id = y.alan AND y.to_status = 4
ORDER BY y.at, y.id;

------------------------------------------------------------------------------------------------ kimlik sayaçları
SELECT setval(pg_get_serial_sequence('tuptakip.customers', 'id'), (SELECT max(id) FROM tuptakip.customers));
SELECT setval(pg_get_serial_sequence('tuptakip.cylinders', 'id'), (SELECT max(id) FROM tuptakip.cylinders));

COMMIT;
ANALYZE tuptakip.customers;
ANALYZE tuptakip.cylinders;
ANALYZE tuptakip.cylindermovements;
ANALYZE tuptakip.vehicles;
