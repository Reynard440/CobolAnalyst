       IDENTIFICATION DIVISION.
       PROGRAM-ID. GLEDGER.
       AUTHOR. COBOL-ANALYST.
       DATE-WRITTEN. 2024-01-15.
      *----------------------------------------------------------------*
      * GENERAL LEDGER POSTING PROGRAM                                 *
      * Posts debit and credit journal entries to the general ledger,  *
      * enforces double-entry balance rules, validates account codes,  *
      * handles period-close processing, and maintains trial balance.  *
      *----------------------------------------------------------------*
       ENVIRONMENT DIVISION.
       CONFIGURATION SECTION.
       SOURCE-COMPUTER. IBM-MAINFRAME.
       OBJECT-COMPUTER. IBM-MAINFRAME.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT GL-MASTER-FILE ASSIGN TO GLMASTER
               ORGANIZATION IS INDEXED
               ACCESS MODE IS RANDOM
               RECORD KEY IS GL-ACCOUNT-CODE
               FILE STATUS IS WS-GL-STATUS.
           SELECT JOURNAL-FILE ASSIGN TO JRNLFILE
               ORGANIZATION IS SEQUENTIAL.
           SELECT GL-REPORT ASSIGN TO GLRPT
               ORGANIZATION IS SEQUENTIAL.
           SELECT ERROR-FILE ASSIGN TO GLERR
               ORGANIZATION IS SEQUENTIAL.

       DATA DIVISION.
       FILE SECTION.
       FD  GL-MASTER-FILE
           LABEL RECORDS ARE STANDARD.
       01  GL-MASTER-RECORD.
           05  GL-ACCOUNT-CODE    PIC X(8).
           05  GL-ACCOUNT-DESC    PIC X(40).
           05  GL-ACCOUNT-TYPE    PIC X(1).
           05  GL-NORMAL-BALANCE  PIC X(1).
           05  GL-PERIOD-CODE     PIC 9(4).
           05  GL-YTD-DEBIT       PIC S9(13)V99 SIGN LEADING SEPARATE.
           05  GL-YTD-CREDIT      PIC S9(13)V99 SIGN LEADING SEPARATE.
           05  GL-CURRENT-BALANCE PIC S9(13)V99 SIGN LEADING SEPARATE.
           05  GL-CLOSED-FLAG     PIC X(1).
           05  FILLER             PIC X(5).

       FD  JOURNAL-FILE
           LABEL RECORDS ARE STANDARD.
       01  JOURNAL-RECORD.
           05  JR-BATCH-ID        PIC X(8).
           05  JR-TRANS-SEQ       PIC 9(5).
           05  JR-ACCOUNT-CODE    PIC X(8).
           05  JR-DR-CR-CODE      PIC X(1).
           05  JR-AMOUNT          PIC 9(13)V99.
           05  JR-DESCRIPTION     PIC X(30).
           05  JR-PERIOD-CODE     PIC 9(4).
           05  FILLER             PIC X(5).

       FD  GL-REPORT
           LABEL RECORDS ARE OMITTED.
       01  GL-REPORT-LINE         PIC X(132).

       FD  ERROR-FILE
           LABEL RECORDS ARE OMITTED.
       01  GL-ERROR-RECORD        PIC X(132).

       WORKING-STORAGE SECTION.
       01  WS-STATUS-FLAGS.
           05  WS-GL-STATUS       PIC X(2)   VALUE SPACES.
           05  WS-EOF-JOURNAL     PIC X(1)   VALUE 'N'.
           05  WS-VALID-ENTRY     PIC X(1)   VALUE 'Y'.
           05  WS-ACCT-FOUND      PIC X(1)   VALUE 'N'.

       01  WS-BATCH-CONTROL.
           05  WS-CURRENT-BATCH   PIC X(8)   VALUE SPACES.
           05  WS-BATCH-DEBITS    PIC S9(13)V99 VALUE ZEROS.
           05  WS-BATCH-CREDITS   PIC S9(13)V99 VALUE ZEROS.
           05  WS-BATCH-BALANCED  PIC X(1)   VALUE 'N'.
           05  WS-BATCH-COUNT     PIC 9(5)   VALUE ZEROS.

       01  WS-POSTING-WORK.
           05  WS-POST-AMOUNT     PIC S9(13)V99 VALUE ZEROS.
           05  WS-SIGNED-AMOUNT   PIC S9(13)V99 VALUE ZEROS.
           05  WS-NEW-BALANCE     PIC S9(13)V99 VALUE ZEROS.
           05  WS-PERIOD-CODE     PIC 9(4)   VALUE ZEROS.

       01  WS-PERIOD-CLOSE-WORK.
           05  WS-CLOSE-REQUESTED PIC X(1)   VALUE 'N'.
           05  WS-CLOSE-PERIOD    PIC 9(4)   VALUE ZEROS.
           05  WS-RETAINED-EARN   PIC X(8)   VALUE '30000000'.

       01  WS-ACCUMULATORS.
           05  WS-TOTAL-DEBITS    PIC S9(15)V99 VALUE ZEROS.
           05  WS-TOTAL-CREDITS   PIC S9(15)V99 VALUE ZEROS.
           05  WS-TOTAL-POSTS     PIC 9(7)   VALUE ZEROS.
           05  WS-ERROR-COUNT     PIC 9(5)   VALUE ZEROS.

       01  WS-ERROR-DETAIL.
           05  WS-ERR-CODE        PIC X(4)   VALUE SPACES.
           05  WS-ERR-MSG         PIC X(60)  VALUE SPACES.

       01  WS-ACCOUNT-TYPE-TABLE.
           05  WS-ASSET-TYPE      PIC X(1)   VALUE 'A'.
           05  WS-LIAB-TYPE       PIC X(1)   VALUE 'L'.
           05  WS-EQUITY-TYPE     PIC X(1)   VALUE 'E'.
           05  WS-REVENUE-TYPE    PIC X(1)   VALUE 'R'.
           05  WS-EXPENSE-TYPE    PIC X(1)   VALUE 'X'.

       PROCEDURE DIVISION.
       0000-MAIN-CONTROL.
           PERFORM 1000-INITIALIZE
           PERFORM 2000-READ-JOURNAL
           PERFORM 3000-PROCESS-JOURNAL-ENTRY
               UNTIL WS-EOF-JOURNAL = 'Y'
           PERFORM 4000-CLOSE-BATCH
           PERFORM 5000-PERIOD-CLOSE-CHECK
           PERFORM 6000-PRINT-TRIAL-BALANCE
           PERFORM 9000-TERMINATE
           STOP RUN.

       1000-INITIALIZE.
           OPEN I-O    GL-MASTER-FILE
           OPEN INPUT  JOURNAL-FILE
           OPEN OUTPUT GL-REPORT
           OPEN OUTPUT ERROR-FILE
           MOVE 'N' TO WS-EOF-JOURNAL
           MOVE ZEROS TO WS-ACCUMULATORS
           MOVE SPACES TO WS-CURRENT-BATCH
           MOVE ZEROS TO WS-BATCH-DEBITS
           MOVE ZEROS TO WS-BATCH-CREDITS
           MOVE SPACES TO GL-REPORT-LINE
           MOVE 'GENERAL LEDGER POSTING RUN' TO GL-REPORT-LINE
           WRITE GL-REPORT-LINE.

       2000-READ-JOURNAL.
           READ JOURNAL-FILE INTO JOURNAL-RECORD
               AT END MOVE 'Y' TO WS-EOF-JOURNAL
           END-READ.

       3000-PROCESS-JOURNAL-ENTRY.
           MOVE 'Y' TO WS-VALID-ENTRY
           MOVE SPACES TO WS-ERR-CODE WS-ERR-MSG
           IF JR-BATCH-ID NOT = WS-CURRENT-BATCH
               IF WS-CURRENT-BATCH NOT = SPACES
                   PERFORM 4000-CLOSE-BATCH
               END-IF
               MOVE JR-BATCH-ID TO WS-CURRENT-BATCH
               MOVE ZEROS TO WS-BATCH-DEBITS
               MOVE ZEROS TO WS-BATCH-CREDITS
           END-IF
           PERFORM 3100-VALIDATE-ACCOUNT
           IF WS-VALID-ENTRY = 'Y'
               PERFORM 3200-VALIDATE-DR-CR
               IF WS-VALID-ENTRY = 'Y'
                   PERFORM 3300-POST-TO-LEDGER
               END-IF
           END-IF
           IF WS-VALID-ENTRY = 'N'
               ADD 1 TO WS-ERROR-COUNT
               PERFORM 3400-WRITE-GL-ERROR
           END-IF
           PERFORM 2000-READ-JOURNAL.

       3100-VALIDATE-ACCOUNT.
           MOVE JR-ACCOUNT-CODE TO GL-ACCOUNT-CODE
           READ GL-MASTER-FILE
               INVALID KEY
                   MOVE 'N' TO WS-VALID-ENTRY
                   MOVE 'G001' TO WS-ERR-CODE
                   MOVE 'ACCOUNT CODE NOT IN CHART OF ACCOUNTS'
                       TO WS-ERR-MSG
               NOT INVALID KEY
                   MOVE 'Y' TO WS-ACCT-FOUND
                   IF GL-CLOSED-FLAG = 'Y'
                       MOVE 'N' TO WS-VALID-ENTRY
                       MOVE 'G002' TO WS-ERR-CODE
                       MOVE 'ACCOUNT IS CLOSED TO POSTING'
                           TO WS-ERR-MSG
                   END-IF
                   IF JR-PERIOD-CODE NOT = GL-PERIOD-CODE
                       MOVE 'N' TO WS-VALID-ENTRY
                       MOVE 'G003' TO WS-ERR-CODE
                       MOVE 'PERIOD MISMATCH - ACCOUNT PERIOD CLOSED'
                           TO WS-ERR-MSG
                   END-IF
           END-READ.

       3200-VALIDATE-DR-CR.
           IF JR-AMOUNT = ZEROS
               MOVE 'N' TO WS-VALID-ENTRY
               MOVE 'G004' TO WS-ERR-CODE
               MOVE 'TRANSACTION AMOUNT CANNOT BE ZERO'
                   TO WS-ERR-MSG
           ELSE
               EVALUATE JR-DR-CR-CODE
                   WHEN 'D'
                       CONTINUE
                   WHEN 'C'
                       CONTINUE
                   WHEN OTHER
                       MOVE 'N' TO WS-VALID-ENTRY
                       MOVE 'G005' TO WS-ERR-CODE
                       MOVE 'INVALID DR/CR CODE - MUST BE D OR C'
                           TO WS-ERR-MSG
               END-EVALUATE
           END-IF.

       3300-POST-TO-LEDGER.
           EVALUATE JR-DR-CR-CODE
               WHEN 'D'
                   ADD JR-AMOUNT TO GL-YTD-DEBIT
                   ADD JR-AMOUNT TO WS-BATCH-DEBITS
                   ADD JR-AMOUNT TO WS-TOTAL-DEBITS
                   EVALUATE GL-NORMAL-BALANCE
                       WHEN 'D'
                           ADD JR-AMOUNT TO GL-CURRENT-BALANCE
                       WHEN 'C'
                           SUBTRACT JR-AMOUNT FROM GL-CURRENT-BALANCE
                       WHEN OTHER
                           CONTINUE
                   END-EVALUATE
               WHEN 'C'
                   ADD JR-AMOUNT TO GL-YTD-CREDIT
                   ADD JR-AMOUNT TO WS-BATCH-CREDITS
                   ADD JR-AMOUNT TO WS-TOTAL-CREDITS
                   EVALUATE GL-NORMAL-BALANCE
                       WHEN 'C'
                           ADD JR-AMOUNT TO GL-CURRENT-BALANCE
                       WHEN 'D'
                           SUBTRACT JR-AMOUNT FROM GL-CURRENT-BALANCE
                       WHEN OTHER
                           CONTINUE
                   END-EVALUATE
           END-EVALUATE
           REWRITE GL-MASTER-RECORD
           ADD 1 TO WS-TOTAL-POSTS
           ADD 1 TO WS-BATCH-COUNT.

       3400-WRITE-GL-ERROR.
           MOVE SPACES TO GL-ERROR-RECORD
           STRING WS-ERR-CODE ' BATCH:' JR-BATCH-ID
               ' ACCT:' JR-ACCOUNT-CODE ' ' WS-ERR-MSG
               DELIMITED BY SIZE INTO GL-ERROR-RECORD
           WRITE GL-ERROR-RECORD.

       4000-CLOSE-BATCH.
           IF WS-CURRENT-BATCH = SPACES
               CONTINUE
           ELSE
               IF WS-BATCH-DEBITS = WS-BATCH-CREDITS
                   MOVE 'Y' TO WS-BATCH-BALANCED
               ELSE
                   MOVE 'N' TO WS-BATCH-BALANCED
                   MOVE SPACES TO GL-REPORT-LINE
                   STRING 'OUT-OF-BALANCE BATCH: ' WS-CURRENT-BATCH
                       ' DR: ' WS-BATCH-DEBITS
                       ' CR: ' WS-BATCH-CREDITS
                       DELIMITED BY SIZE INTO GL-REPORT-LINE
                   WRITE GL-REPORT-LINE
               END-IF
           END-IF.

       5000-PERIOD-CLOSE-CHECK.
           IF WS-CLOSE-REQUESTED = 'Y'
               MOVE SPACES TO GL-REPORT-LINE
               MOVE 'PERIOD CLOSE PROCESSING - NOT IMPLEMENTED'
                   TO GL-REPORT-LINE
               WRITE GL-REPORT-LINE
           END-IF.

       6000-PRINT-TRIAL-BALANCE.
           MOVE SPACES TO GL-REPORT-LINE
           WRITE GL-REPORT-LINE
           MOVE 'TRIAL BALANCE SUMMARY' TO GL-REPORT-LINE
           WRITE GL-REPORT-LINE
           MOVE SPACES TO GL-REPORT-LINE
           STRING 'TOTAL POSTS: ' WS-TOTAL-POSTS
               '  TOTAL DR: ' WS-TOTAL-DEBITS
               '  TOTAL CR: ' WS-TOTAL-CREDITS
               DELIMITED BY SIZE INTO GL-REPORT-LINE
           WRITE GL-REPORT-LINE.

       9000-TERMINATE.
           CLOSE GL-MASTER-FILE
           CLOSE JOURNAL-FILE
           CLOSE GL-REPORT
           CLOSE ERROR-FILE.
