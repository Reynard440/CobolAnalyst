       IDENTIFICATION DIVISION.
       PROGRAM-ID. CUSTMAST.
       AUTHOR. COBOL-ANALYST.
       DATE-WRITTEN. 2024-01-15.
      *----------------------------------------------------------------*
      * CUSTOMER MASTER MAINTENANCE PROGRAM                            *
      * Handles add, update, and delete transactions for the customer  *
      * master file. Validates for duplicates, checks address format,  *
      * enforces mandatory fields, and logs all changes.               *
      *----------------------------------------------------------------*
       ENVIRONMENT DIVISION.
       CONFIGURATION SECTION.
       SOURCE-COMPUTER. IBM-MAINFRAME.
       OBJECT-COMPUTER. IBM-MAINFRAME.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT CUSTOMER-MASTER ASSIGN TO CUSTMAS
               ORGANIZATION IS INDEXED
               ACCESS MODE IS RANDOM
               RECORD KEY IS CM-CUSTOMER-ID
               FILE STATUS IS WS-CUST-STATUS.
           SELECT TRANSACTION-FILE ASSIGN TO CUSTTRAN
               ORGANIZATION IS SEQUENTIAL.
           SELECT AUDIT-LOG ASSIGN TO CUSTAUDT
               ORGANIZATION IS SEQUENTIAL.
           SELECT ERROR-REPORT ASSIGN TO CUSTERR
               ORGANIZATION IS SEQUENTIAL.

       DATA DIVISION.
       FILE SECTION.
       FD  CUSTOMER-MASTER
           LABEL RECORDS ARE STANDARD.
       01  CUSTOMER-RECORD.
           05  CM-CUSTOMER-ID     PIC X(8).
           05  CM-FIRST-NAME      PIC X(20).
           05  CM-LAST-NAME       PIC X(25).
           05  CM-ADDRESS-LINE-1  PIC X(35).
           05  CM-ADDRESS-LINE-2  PIC X(35).
           05  CM-CITY            PIC X(25).
           05  CM-STATE-CODE      PIC X(2).
           05  CM-ZIP-CODE        PIC X(10).
           05  CM-PHONE           PIC X(12).
           05  CM-EMAIL           PIC X(50).
           05  CM-CREDIT-LIMIT    PIC 9(7)V99.
           05  CM-STATUS-CODE     PIC X(1).
           05  CM-CREATE-DATE     PIC 9(8).
           05  CM-UPDATE-DATE     PIC 9(8).

       FD  TRANSACTION-FILE
           LABEL RECORDS ARE STANDARD.
       01  TRANSACTION-RECORD.
           05  TR-ACTION-CODE     PIC X(1).
           05  TR-CUSTOMER-ID     PIC X(8).
           05  TR-FIRST-NAME      PIC X(20).
           05  TR-LAST-NAME       PIC X(25).
           05  TR-ADDRESS-LINE-1  PIC X(35).
           05  TR-ADDRESS-LINE-2  PIC X(35).
           05  TR-CITY            PIC X(25).
           05  TR-STATE-CODE      PIC X(2).
           05  TR-ZIP-CODE        PIC X(10).
           05  TR-PHONE           PIC X(12).
           05  TR-EMAIL           PIC X(50).
           05  TR-CREDIT-LIMIT    PIC 9(7)V99.
           05  FILLER             PIC X(3).

       FD  AUDIT-LOG
           LABEL RECORDS ARE OMITTED.
       01  AUDIT-RECORD           PIC X(132).

       FD  ERROR-REPORT
           LABEL RECORDS ARE OMITTED.
       01  ERROR-RECORD           PIC X(132).

       WORKING-STORAGE SECTION.
       01  WS-STATUS-FIELDS.
           05  WS-CUST-STATUS     PIC X(2)   VALUE SPACES.
           05  WS-EOF-TRANS       PIC X(1)   VALUE 'N'.
           05  WS-VALID-FLAG      PIC X(1)   VALUE 'Y'.
           05  WS-DUPLICATE-FLAG  PIC X(1)   VALUE 'N'.

       01  WS-ERROR-DETAIL.
           05  WS-ERROR-CODE      PIC X(4)   VALUE SPACES.
           05  WS-ERROR-MSG       PIC X(60)  VALUE SPACES.

       01  WS-CURRENT-DATE-INFO.
           05  WS-CURRENT-DATE    PIC 9(8)   VALUE ZEROS.
           05  WS-YEAR            PIC 9(4)   VALUE ZEROS.
           05  WS-MONTH           PIC 9(2)   VALUE ZEROS.
           05  WS-DAY             PIC 9(2)   VALUE ZEROS.

       01  WS-COUNTERS.
           05  WS-ADD-COUNT       PIC 9(5)   VALUE ZEROS.
           05  WS-UPDATE-COUNT    PIC 9(5)   VALUE ZEROS.
           05  WS-DELETE-COUNT    PIC 9(5)   VALUE ZEROS.
           05  WS-ERROR-COUNT     PIC 9(5)   VALUE ZEROS.
           05  WS-TRANS-COUNT     PIC 9(5)   VALUE ZEROS.

       01  WS-ZIP-WORK.
           05  WS-ZIP-NUMERIC     PIC 9(5)   VALUE ZEROS.
           05  WS-ZIP-CHAR        PIC X(5)   VALUE SPACES.

       01  WS-VALID-STATES.
           05  FILLER             PIC X(2)   VALUE 'AL'.
           05  FILLER             PIC X(2)   VALUE 'AK'.
           05  FILLER             PIC X(2)   VALUE 'CA'.
           05  FILLER             PIC X(2)   VALUE 'FL'.
           05  FILLER             PIC X(2)   VALUE 'GA'.
           05  FILLER             PIC X(2)   VALUE 'IL'.
           05  FILLER             PIC X(2)   VALUE 'NY'.
           05  FILLER             PIC X(2)   VALUE 'TX'.
           05  FILLER             PIC X(2)   VALUE 'WA'.
           05  FILLER             PIC X(2)   VALUE 'XX'.
       01  WS-STATE-TABLE REDEFINES WS-VALID-STATES.
           05  WS-STATE-ENTRY     PIC X(2)  OCCURS 10 TIMES.

       01  WS-STATE-INDEX         PIC 9(2)   VALUE ZEROS.
       01  WS-STATE-FOUND         PIC X(1)   VALUE 'N'.

       PROCEDURE DIVISION.
       0000-MAIN-CONTROL.
           PERFORM 1000-INITIALIZE
           PERFORM 2000-READ-TRANSACTION
           PERFORM 3000-PROCESS-TRANSACTION
               UNTIL WS-EOF-TRANS = 'Y'
           PERFORM 4000-PRINT-SUMMARY
           PERFORM 9000-TERMINATE
           STOP RUN.

       1000-INITIALIZE.
           OPEN I-O    CUSTOMER-MASTER
           OPEN INPUT  TRANSACTION-FILE
           OPEN OUTPUT AUDIT-LOG
           OPEN OUTPUT ERROR-REPORT
           ACCEPT WS-CURRENT-DATE FROM DATE YYYYMMDD
           MOVE 'N' TO WS-EOF-TRANS
           MOVE ZEROS TO WS-COUNTERS.

       2000-READ-TRANSACTION.
           READ TRANSACTION-FILE INTO TRANSACTION-RECORD
               AT END MOVE 'Y' TO WS-EOF-TRANS
           END-READ
           IF WS-EOF-TRANS = 'N'
               ADD 1 TO WS-TRANS-COUNT.

       3000-PROCESS-TRANSACTION.
           MOVE 'Y' TO WS-VALID-FLAG
           MOVE SPACES TO WS-ERROR-CODE WS-ERROR-MSG
           PERFORM 3100-VALIDATE-COMMON-FIELDS
           IF WS-VALID-FLAG = 'Y'
               EVALUATE TR-ACTION-CODE
                   WHEN 'A'
                       PERFORM 3200-ADD-CUSTOMER
                   WHEN 'U'
                       PERFORM 3300-UPDATE-CUSTOMER
                   WHEN 'D'
                       PERFORM 3400-DELETE-CUSTOMER
                   WHEN OTHER
                       MOVE 'N' TO WS-VALID-FLAG
                       MOVE 'E001' TO WS-ERROR-CODE
                       MOVE 'INVALID ACTION CODE'
                           TO WS-ERROR-MSG
               END-EVALUATE
           END-IF
           IF WS-VALID-FLAG = 'N'
               ADD 1 TO WS-ERROR-COUNT
               PERFORM 3500-WRITE-ERROR
           END-IF
           PERFORM 2000-READ-TRANSACTION.

       3100-VALIDATE-COMMON-FIELDS.
           IF TR-CUSTOMER-ID = SPACES
               MOVE 'N' TO WS-VALID-FLAG
               MOVE 'E002' TO WS-ERROR-CODE
               MOVE 'CUSTOMER ID IS REQUIRED' TO WS-ERROR-MSG
           ELSE
               IF TR-ACTION-CODE NOT = 'D'
                   IF TR-LAST-NAME = SPACES
                       MOVE 'N' TO WS-VALID-FLAG
                       MOVE 'E003' TO WS-ERROR-CODE
                       MOVE 'LAST NAME IS REQUIRED' TO WS-ERROR-MSG
                   ELSE
                       PERFORM 3110-VALIDATE-ADDRESS
                   END-IF
               END-IF
           END-IF.

       3110-VALIDATE-ADDRESS.
           IF TR-ADDRESS-LINE-1 = SPACES
               MOVE 'N' TO WS-VALID-FLAG
               MOVE 'E004' TO WS-ERROR-CODE
               MOVE 'ADDRESS LINE 1 IS REQUIRED' TO WS-ERROR-MSG
           ELSE
               MOVE 'N' TO WS-STATE-FOUND
               PERFORM VARYING WS-STATE-INDEX FROM 1 BY 1
                   UNTIL WS-STATE-INDEX > 10
                   IF TR-STATE-CODE =
                       WS-STATE-ENTRY(WS-STATE-INDEX)
                       MOVE 'Y' TO WS-STATE-FOUND
                   END-IF
               END-PERFORM
               IF WS-STATE-FOUND = 'N'
                   MOVE 'N' TO WS-VALID-FLAG
                   MOVE 'E005' TO WS-ERROR-CODE
                   MOVE 'INVALID STATE CODE' TO WS-ERROR-MSG
               END-IF
           END-IF.

       3200-ADD-CUSTOMER.
           MOVE TR-CUSTOMER-ID TO CM-CUSTOMER-ID
           READ CUSTOMER-MASTER
               INVALID KEY MOVE 'N' TO WS-DUPLICATE-FLAG
               NOT INVALID KEY MOVE 'Y' TO WS-DUPLICATE-FLAG
           END-READ
           IF WS-DUPLICATE-FLAG = 'Y'
               MOVE 'N' TO WS-VALID-FLAG
               MOVE 'E006' TO WS-ERROR-CODE
               MOVE 'CUSTOMER ID ALREADY EXISTS' TO WS-ERROR-MSG
           ELSE
               PERFORM 3210-POPULATE-CUSTOMER
               WRITE CUSTOMER-RECORD
               ADD 1 TO WS-ADD-COUNT
               PERFORM 3600-WRITE-AUDIT
           END-IF.

       3210-POPULATE-CUSTOMER.
           MOVE TR-CUSTOMER-ID    TO CM-CUSTOMER-ID
           MOVE TR-FIRST-NAME     TO CM-FIRST-NAME
           MOVE TR-LAST-NAME      TO CM-LAST-NAME
           MOVE TR-ADDRESS-LINE-1 TO CM-ADDRESS-LINE-1
           MOVE TR-ADDRESS-LINE-2 TO CM-ADDRESS-LINE-2
           MOVE TR-CITY           TO CM-CITY
           MOVE TR-STATE-CODE     TO CM-STATE-CODE
           MOVE TR-ZIP-CODE       TO CM-ZIP-CODE
           MOVE TR-PHONE          TO CM-PHONE
           MOVE TR-EMAIL          TO CM-EMAIL
           MOVE TR-CREDIT-LIMIT   TO CM-CREDIT-LIMIT
           MOVE 'A'               TO CM-STATUS-CODE
           MOVE WS-CURRENT-DATE   TO CM-CREATE-DATE
           MOVE WS-CURRENT-DATE   TO CM-UPDATE-DATE.

       3300-UPDATE-CUSTOMER.
           MOVE TR-CUSTOMER-ID TO CM-CUSTOMER-ID
           READ CUSTOMER-MASTER
               INVALID KEY
                   MOVE 'N' TO WS-VALID-FLAG
                   MOVE 'E007' TO WS-ERROR-CODE
                   MOVE 'CUSTOMER ID NOT FOUND' TO WS-ERROR-MSG
               NOT INVALID KEY
                   IF CM-STATUS-CODE = 'D'
                       MOVE 'N' TO WS-VALID-FLAG
                       MOVE 'E008' TO WS-ERROR-CODE
                       MOVE 'CANNOT UPDATE DELETED CUSTOMER'
                           TO WS-ERROR-MSG
                   ELSE
                       PERFORM 3210-POPULATE-CUSTOMER
                       MOVE 'A' TO CM-STATUS-CODE
                       REWRITE CUSTOMER-RECORD
                       ADD 1 TO WS-UPDATE-COUNT
                       PERFORM 3600-WRITE-AUDIT
                   END-IF
           END-READ.

       3400-DELETE-CUSTOMER.
           MOVE TR-CUSTOMER-ID TO CM-CUSTOMER-ID
           READ CUSTOMER-MASTER
               INVALID KEY
                   MOVE 'N' TO WS-VALID-FLAG
                   MOVE 'E007' TO WS-ERROR-CODE
                   MOVE 'CUSTOMER ID NOT FOUND' TO WS-ERROR-MSG
               NOT INVALID KEY
                   MOVE 'D' TO CM-STATUS-CODE
                   MOVE WS-CURRENT-DATE TO CM-UPDATE-DATE
                   REWRITE CUSTOMER-RECORD
                   ADD 1 TO WS-DELETE-COUNT
                   PERFORM 3600-WRITE-AUDIT
           END-READ.

       3500-WRITE-ERROR.
           MOVE SPACES TO ERROR-RECORD
           STRING WS-ERROR-CODE ' ' TR-CUSTOMER-ID ' '
               TR-ACTION-CODE ' ' WS-ERROR-MSG
               DELIMITED BY SIZE INTO ERROR-RECORD
           WRITE ERROR-RECORD.

       3600-WRITE-AUDIT.
           MOVE SPACES TO AUDIT-RECORD
           STRING TR-ACTION-CODE ' ' TR-CUSTOMER-ID ' '
               WS-CURRENT-DATE ' OK'
               DELIMITED BY SIZE INTO AUDIT-RECORD
           WRITE AUDIT-RECORD.

       4000-PRINT-SUMMARY.
           MOVE SPACES TO AUDIT-RECORD
           WRITE AUDIT-RECORD
           STRING 'ADDS: ' WS-ADD-COUNT
               '  UPDATES: ' WS-UPDATE-COUNT
               '  DELETES: ' WS-DELETE-COUNT
               '  ERRORS: ' WS-ERROR-COUNT
               DELIMITED BY SIZE INTO AUDIT-RECORD
           WRITE AUDIT-RECORD.

       9000-TERMINATE.
           CLOSE CUSTOMER-MASTER
           CLOSE TRANSACTION-FILE
           CLOSE AUDIT-LOG
           CLOSE ERROR-REPORT.
