-- DİKKAT: ArsanTüpTakip'teki TÜM tüp, müşteri, araç, sefer, sayım ve hareket kayıtlarını siler (kullanıcılar ve yetkiler kalır).
-- Yalnızca deneme aktarımını kalıcı geçişten önce temizlemek için; run-import.sh --reset ile ve yedek alındıktan sonra çalışır.
\set ON_ERROR_STOP on
BEGIN;
TRUNCATE tuptakip.cylindertripcylinders, tuptakip.cylindertripvehicles, tuptakip.cylindertrips, tuptakip.cylinderaudits,
         tuptakip.cylindermovements, tuptakip.cylinders, tuptakip.customers, tuptakip.vehicles RESTART IDENTITY;
COMMIT;
