       IDENTIFICATION DIVISION.
       PROGRAM-ID. INVCTL.
       AUTHOR. COBOL-ANALYST.
       DATE-WRITTEN. 2024-01-15.
      *----------------------------------------------------------------*
      * INVENTORY CONTROL PROGRAM                                      *
      * Manages stock levels, validates movements, checks reorder      *
      * points, performs supplier code lookup, and adjusts quantities. *
      *----------------------------------------------------------------*
       ENVIRONMENT DIVISION.
       CONFIGURATION SECTION.
       SOURCE-COMPUTER. IBM-MAINFRAME.
       OBJECT-COMPUTER. IBM-MAINFRAME.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT INVENTORY-FILE ASSIGN TO INVFILE
               ORGANIZATION IS INDEXED
               ACCESS MODE IS RANDOM
               RECORD KEY IS INV-ITEM-CODE
               FILE STATUS IS WS-FILE-STATUS.
           SELECT MOVEMENT-FILE ASSIGN TO MOVFILE
               ORGANIZATION IS SEQUENTIAL.
           SELECT SUPPLIER-FILE ASSIGN TO SUPFILE
               ORGANIZATION IS INDEXED
               ACCESS MODE IS RANDOM
               RECORD KEY IS SUP-CODE.
           SELECT REPORT-FILE ASSIGN TO INVRPT
               ORGANIZATION IS SEQUENTIAL.

       DATA DIVISION.
       FILE SECTION.
       FD  INVENTORY-FILE
           LABEL RECORDS ARE STANDARD.
       01  INVENTORY-RECORD.
           05  INV-ITEM-CODE      PIC X(10).
           05  INV-DESCRIPTION    PIC X(30).
           05  INV-QTY-ON-HAND    PIC 9(7).
           05  INV-QTY-ON-ORDER   PIC 9(7).
           05  INV-REORDER-POINT  PIC 9(7).
           05  INV-REORDER-QTY    PIC 9(7).
           05  INV-SUPPLIER-CODE  PIC X(6).
           05  INV-UNIT-COST      PIC 9(7)V99.
           05  FILLER             PIC X(10).

       FD  MOVEMENT-FILE
           LABEL RECORDS ARE STANDARD.
       01  MOVEMENT-RECORD.
           05  MOV-ITEM-CODE      PIC X(10).
           05  MOV-TRANS-TYPE     PIC X(2).
           05  MOV-QUANTITY       PIC 9(7).
           05  MOV-REFERENCE      PIC X(12).
           05  MOV-DATE           PIC 9(8).
           05  FILLER             PIC X(41).

       FD  SUPPLIER-FILE
           LABEL RECORDS ARE STANDARD.
       01  SUPPLIER-RECORD.
           05  SUP-CODE           PIC X(6).
           05  SUP-NAME           PIC X(30).
           05  SUP-LEAD-DAYS      PIC 9(3).
           05  SUP-ACTIVE-FLAG    PIC X(1).
           05  FILLER             PIC X(40).

       FD  REPORT-FILE
           LABEL RECORDS ARE OMITTED.
       01  REPORT-LINE            PIC X(132).

       WORKING-STORAGE SECTION.
       01  WS-STATUS-FLAGS.
           05  WS-FILE-STATUS     PIC X(2)   VALUE SPACES.
           05  WS-EOF-MOVEMENT    PIC X(1)   VALUE 'N'.
           05  WS-RECORD-FOUND    PIC X(1)   VALUE 'N'.
           05  WS-VALID-TRANS     PIC X(1)   VALUE 'Y'.

       01  WS-TRANSACTION-WORK.
           05  WS-ITEM-CODE       PIC X(10)  VALUE SPACES.
           05  WS-TRANS-TYPE      PIC X(2)   VALUE SPACES.
           05  WS-MOVE-QTY        PIC 9(7)   VALUE ZEROS.
           05  WS-NEW-QTY         PIC 9(7)   VALUE ZEROS.
           05  WS-SUPPLIER-CODE   PIC X(6)   VALUE SPACES.

       01  WS-REORDER-WORK.
           05  WS-REORDER-NEEDED  PIC X(1)   VALUE 'N'.
           05  WS-AVAILABLE-QTY   PIC 9(7)   VALUE ZEROS.

       01  WS-COUNTERS.
           05  WS-RECEIPT-COUNT   PIC 9(5)   VALUE ZEROS.
           05  WS-ISSUE-COUNT     PIC 9(5)   VALUE ZEROS.
           05  WS-ADJUST-COUNT    PIC 9(5)   VALUE ZEROS.
           05  WS-ERROR-COUNT     PIC 9(5)   VALUE ZEROS.
           05  WS-REORDER-COUNT   PIC 9(5)   VALUE ZEROS.

       01  WS-ERROR-MESSAGE       PIC X(60)  VALUE SPACES.

       01  WS-VALID-TRANS-TYPES.
           05  WS-TYPE-RECEIPT    PIC X(2)   VALUE 'RC'.
           05  WS-TYPE-ISSUE      PIC X(2)   VALUE 'IS'.
           05  WS-TYPE-ADJUST     PIC X(2)   VALUE 'AJ'.
           05  WS-TYPE-RETURN     PIC X(2)   VALUE 'RT'.

       PROCEDURE DIVISION.
       0000-MAIN-CONTROL.
           PERFORM 1000-INITIALIZE
           PERFORM 2000-READ-MOVEMENT
           PERFORM 3000-PROCESS-MOVEMENT
               UNTIL WS-EOF-MOVEMENT = 'Y'
           PERFORM 4000-PRINT-SUMMARY
           PERFORM 9000-TERMINATE
           STOP RUN.

       1000-INITIALIZE.
           OPEN I-O    INVENTORY-FILE
           OPEN INPUT  MOVEMENT-FILE
           OPEN INPUT  SUPPLIER-FILE
           OPEN OUTPUT REPORT-FILE
           MOVE 'N' TO WS-EOF-MOVEMENT
           MOVE ZEROS TO WS-COUNTERS
           MOVE SPACES TO REPORT-LINE
           MOVE 'INVENTORY MOVEMENT REPORT' TO REPORT-LINE
           WRITE REPORT-LINE.

       2000-READ-MOVEMENT.
           READ MOVEMENT-FILE INTO MOVEMENT-RECORD
               AT END MOVE 'Y' TO WS-EOF-MOVEMENT
           END-READ.

       3000-PROCESS-MOVEMENT.
           MOVE 'Y' TO WS-VALID-TRANS
           MOVE SPACES TO WS-ERROR-MESSAGE
           MOVE MOV-ITEM-CODE   TO WS-ITEM-CODE
           MOVE MOV-TRANS-TYPE  TO WS-TRANS-TYPE
           MOVE MOV-QUANTITY    TO WS-MOVE-QTY
           PERFORM 3100-VALIDATE-TRANS-TYPE
           IF WS-VALID-TRANS = 'Y'
               PERFORM 3200-LOOKUP-INVENTORY
               IF WS-RECORD-FOUND = 'Y'
                   PERFORM 3300-VALIDATE-QUANTITY
                   IF WS-VALID-TRANS = 'Y'
                       PERFORM 3400-APPLY-MOVEMENT
                       PERFORM 3500-CHECK-REORDER
                   END-IF
               ELSE
                   MOVE 'ITEM CODE NOT FOUND IN INVENTORY'
                       TO WS-ERROR-MESSAGE
                   MOVE 'N' TO WS-VALID-TRANS
               END-IF
           END-IF
           IF WS-VALID-TRANS = 'N'
               ADD 1 TO WS-ERROR-COUNT
               PERFORM 3600-PRINT-ERROR
           END-IF
           PERFORM 2000-READ-MOVEMENT.

       3100-VALIDATE-TRANS-TYPE.
           EVALUATE WS-TRANS-TYPE
               WHEN 'RC'
                   CONTINUE
               WHEN 'IS'
                   CONTINUE
               WHEN 'AJ'
                   CONTINUE
               WHEN 'RT'
                   CONTINUE
               WHEN OTHER
                   MOVE 'N' TO WS-VALID-TRANS
                   MOVE 'INVALID TRANSACTION TYPE' TO WS-ERROR-MESSAGE
           END-EVALUATE.

       3200-LOOKUP-INVENTORY.
           MOVE WS-ITEM-CODE TO INV-ITEM-CODE
           READ INVENTORY-FILE
               INVALID KEY MOVE 'N' TO WS-RECORD-FOUND
               NOT INVALID KEY MOVE 'Y' TO WS-RECORD-FOUND
           END-READ.

       3300-VALIDATE-QUANTITY.
           IF WS-MOVE-QTY = ZEROS
               MOVE 'N' TO WS-VALID-TRANS
               MOVE 'QUANTITY CANNOT BE ZERO' TO WS-ERROR-MESSAGE
           ELSE
               IF WS-TRANS-TYPE = 'IS'
                   IF WS-MOVE-QTY > INV-QTY-ON-HAND
                       MOVE 'N' TO WS-VALID-TRANS
                       MOVE 'INSUFFICIENT STOCK FOR ISSUE'
                           TO WS-ERROR-MESSAGE
                   END-IF
               END-IF
           END-IF.

       3400-APPLY-MOVEMENT.
           EVALUATE WS-TRANS-TYPE
               WHEN 'RC'
                   ADD WS-MOVE-QTY TO INV-QTY-ON-HAND
                   ADD 1 TO WS-RECEIPT-COUNT
               WHEN 'IS'
                   SUBTRACT WS-MOVE-QTY FROM INV-QTY-ON-HAND
                   ADD 1 TO WS-ISSUE-COUNT
               WHEN 'AJ'
                   MOVE WS-MOVE-QTY TO INV-QTY-ON-HAND
                   ADD 1 TO WS-ADJUST-COUNT
               WHEN 'RT'
                   ADD WS-MOVE-QTY TO INV-QTY-ON-HAND
                   ADD 1 TO WS-RECEIPT-COUNT
           END-EVALUATE
           REWRITE INVENTORY-RECORD.

       3500-CHECK-REORDER.
           MOVE 'N' TO WS-REORDER-NEEDED
           ADD INV-QTY-ON-HAND INV-QTY-ON-ORDER
               GIVING WS-AVAILABLE-QTY
           IF WS-AVAILABLE-QTY <= INV-REORDER-POINT
               MOVE 'Y' TO WS-REORDER-NEEDED
               ADD 1 TO WS-REORDER-COUNT
               MOVE INV-SUPPLIER-CODE TO WS-SUPPLIER-CODE
               PERFORM 3510-LOOKUP-SUPPLIER
               PERFORM 3520-PRINT-REORDER-NOTICE
           END-IF.

       3510-LOOKUP-SUPPLIER.
           MOVE WS-SUPPLIER-CODE TO SUP-CODE
           READ SUPPLIER-FILE
               INVALID KEY
                   MOVE 'UNKNOWN' TO SUP-NAME
               NOT INVALID KEY
                   CONTINUE
           END-READ.

       3520-PRINT-REORDER-NOTICE.
           MOVE SPACES TO REPORT-LINE
           STRING 'REORDER: ' INV-ITEM-CODE ' QTY: '
               INV-REORDER-QTY ' SUPPLIER: ' SUP-NAME
               DELIMITED BY SIZE
               INTO REPORT-LINE
           WRITE REPORT-LINE.

       3600-PRINT-ERROR.
           MOVE SPACES TO REPORT-LINE
           STRING 'ERROR: ' WS-ITEM-CODE ' ' WS-ERROR-MESSAGE
               DELIMITED BY SIZE
               INTO REPORT-LINE
           WRITE REPORT-LINE.

       4000-PRINT-SUMMARY.
           MOVE SPACES TO REPORT-LINE
           WRITE REPORT-LINE
           STRING 'RECEIPTS: ' WS-RECEIPT-COUNT
               '  ISSUES: ' WS-ISSUE-COUNT
               '  ADJUSTMENTS: ' WS-ADJUST-COUNT
               '  ERRORS: ' WS-ERROR-COUNT
               DELIMITED BY SIZE INTO REPORT-LINE
           WRITE REPORT-LINE.

       9000-TERMINATE.
           CLOSE INVENTORY-FILE
           CLOSE MOVEMENT-FILE
           CLOSE SUPPLIER-FILE
           CLOSE REPORT-FILE.
