DECLARE @ПланОбмена number = 0
DECLARE @Метаданные object = SELECT Тип = '', Имя = '', ПолноеИмя = ''

PRINT '[ПЛАН ОБМЕНА] {' + @ПланОбмена + '} Правила обмена не определены: ' + @Метаданные.ПолноеИмя