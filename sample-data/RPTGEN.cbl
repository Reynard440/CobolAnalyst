       IDENTIFICATION DIVISION.
       PROGRAM-ID. RPTGEN.
       AUTHOR. COBOL-ANALYST.
       DATE-WRITTEN. 2024-01-15.
      *----------------------------------------------------------------*
      * REPORT GENERATION PROGRAM                                      *
      * Reads a sorted transaction file, applies date range filtering, *
      * accumulates subtotals by department and region, handles page   *
      * breaks, and produces a formatted columnar report.              *
      *----------------------------------------------------------------*
       ENVIRONMENT DIVISION.
       CONFIGURATION SECTION.
       SOURCE-COMPUTER. IBM-MAINFRAME.
       OBJECT-COMPUTER. IBM-MAINFRAME.
       INPUT-OUTPUT SECTION.
       FILE-CONTROL.
           SELECT TRANSACTION-FILE ASSIGN TO RPTDATA
               ORGANIZATION IS SEQUENTIAL.
           SELECT REPORT-OUTPUT ASSIGN TO RPTOUT
               ORGANIZATION IS SEQUENTIAL.
           SELECT PARAMETER-FILE ASSIGN TO RPTPARM
               ORGANIZATION IS SEQUENTIAL.

       DATA DIVISION.
       FILE SECTION.
       FD  TRANSACTION-FILE
           LABEL RECORDS ARE STANDARD
           RECORD CONTAINS 120 CHARACTERS.
       01  TRANS-RECORD.
           05  TR-REGION-CODE     PIC X(4).
           05  TR-DEPT-CODE       PIC X(6).
           05  TR-ACCOUNT-CODE    PIC X(8).
           05  TR-TRANS-DATE      PIC 9(8).
           05  TR-DESCRIPTION     PIC X(30).
           05  TR-AMOUNT          PIC S9(9)V99 SIGN LEADING SEPARATE.
           05  TR-TRANS-TYPE      PIC X(4).
           05  TR-REFERENCE       PIC X(15).
           05  FILLER             PIC X(30).

       FD  REPORT-OUTPUT
           LABEL RECORDS ARE OMITTED
           RECORD CONTAINS 132 CHARACTERS.
       01  RPT-LINE               PIC X(132).

       FD  PARAMETER-FILE
           LABEL RECORDS ARE STANDARD
           RECORD CONTAINS 80 CHARACTERS.
       01  PARM-RECORD.
           05  PARM-FROM-DATE     PIC 9(8).
           05  PARM-TO-DATE       PIC 9(8).
           05  PARM-REGION-FILTER PIC X(4).
           05  PARM-LINES-PER-PG  PIC 9(3).
           05  FILLER             PIC X(57).

       WORKING-STORAGE SECTION.
       01  WS-STATUS-FLAGS.
           05  WS-EOF-TRANS       PIC X(1)   VALUE 'N'.
           05  WS-EOF-PARM        PIC X(1)   VALUE 'N'.
           05  WS-IN-RANGE        PIC X(1)   VALUE 'N'.
           05  WS-FIRST-RECORD    PIC X(1)   VALUE 'Y'.

       01  WS-PARAMETERS.
           05  WS-FROM-DATE       PIC 9(8)   VALUE ZEROS.
           05  WS-TO-DATE         PIC 9(8)   VALUE ZEROS.
           05  WS-REGION-FILTER   PIC X(4)   VALUE SPACES.
           05  WS-LINES-PER-PAGE  PIC 9(3)   VALUE 055.

       01  WS-BREAK-CONTROLS.
           05  WS-PREV-REGION     PIC X(4)   VALUE SPACES.
           05  WS-PREV-DEPT       PIC X(6)   VALUE SPACES.
           05  WS-CURR-REGION     PIC X(4)   VALUE SPACES.
           05  WS-CURR-DEPT       PIC X(6)   VALUE SPACES.
           05  WS-REGION-BREAK    PIC X(1)   VALUE 'N'.
           05  WS-DEPT-BREAK      PIC X(1)   VALUE 'N'.

       01  WS-SUBTOTALS.
           05  WS-DEPT-TOTAL      PIC S9(11)V99 VALUE ZEROS.
           05  WS-REGION-TOTAL    PIC S9(11)V99 VALUE ZEROS.
           05  WS-GRAND-TOTAL     PIC S9(13)V99 VALUE ZEROS.
           05  WS-DEPT-COUNT      PIC 9(7)   VALUE ZEROS.
           05  WS-REGION-COUNT    PIC 9(7)   VALUE ZEROS.
           05  WS-GRAND-COUNT     PIC 9(9)   VALUE ZEROS.
           05  WS-FILTERED-COUNT  PIC 9(9)   VALUE ZEROS.

       01  WS-PAGE-CONTROL.
           05  WS-PAGE-NUMBER     PIC 9(4)   VALUE ZEROS.
           05  WS-LINE-COUNT      PIC 9(3)   VALUE ZEROS.
           05  WS-LINES-USED      PIC 9(3)   VALUE ZEROS.

       01  WS-DETAIL-LINE.
           05  WS-DL-DATE         PIC 9(8).
           05  FILLER             PIC X(2)   VALUE SPACES.
           05  WS-DL-ACCOUNT      PIC X(8).
           05  FILLER             PIC X(2)   VALUE SPACES.
           05  WS-DL-DESC         PIC X(30).
           05  FILLER             PIC X(2)   VALUE SPACES.
           05  WS-DL-TYPE         PIC X(4).
           05  FILLER             PIC X(2)   VALUE SPACES.
           05  WS-DL-AMOUNT       PIC -(9)9.99.
           05  FILLER             PIC X(2)   VALUE SPACES.
           05  WS-DL-REF          PIC X(15).

       01  WS-SUBTOTAL-LINE.
           05  FILLER             PIC X(40)  VALUE SPACES.
           05  WS-SL-LABEL        PIC X(30).
           05  FILLER             PIC X(2)   VALUE SPACES.
           05  WS-SL-COUNT        PIC ZZZ,ZZ9.
           05  FILLER             PIC X(2)   VALUE SPACES.
           05  WS-SL-AMOUNT       PIC -$(10)9.99.

       01  WS-HEADER-LINE-1.
           05  FILLER             PIC X(10)  VALUE 'DATE RANGE'.
           05  WS-HDR-FROM        PIC 9(8).
           05  FILLER             PIC X(4)   VALUE ' TO '.
           05  WS-HDR-TO          PIC 9(8).
           05  FILLER             PIC X(10)  VALUE SPACES.
           05  FILLER             PIC X(7)   VALUE 'PAGE:  '.
           05  WS-HDR-PAGE        PIC ZZZZ.

       01  WS-HEADER-LINE-2.
           05  FILLER             PIC X(8)   VALUE 'DATE    '.
           05  FILLER             PIC X(10)  VALUE 'ACCOUNT   '.
           05  FILLER             PIC X(32)  VALUE 'DESCRIPTION                     '.
           05  FILLER             PIC X(6)   VALUE 'TYPE  '.
           05  FILLER             PIC X(14)  VALUE 'AMOUNT        '.
           05  FILLER             PIC X(15)  VALUE 'REFERENCE      '.

       PROCEDURE DIVISION.
       0000-MAIN-CONTROL.
           PERFORM 1000-INITIALIZE
           PERFORM 2000-READ-PARAMETERS
           PERFORM 2100-READ-TRANSACTION
           PERFORM 3000-PROCESS-RECORD
               UNTIL WS-EOF-TRANS = 'Y'
           PERFORM 4000-FINAL-TOTALS
           PERFORM 9000-TERMINATE
           STOP RUN.

       1000-INITIALIZE.
           OPEN INPUT  TRANSACTION-FILE
           OPEN INPUT  PARAMETER-FILE
           OPEN OUTPUT REPORT-OUTPUT
           MOVE 'N' TO WS-EOF-TRANS
           MOVE 'N' TO WS-EOF-PARM
           MOVE 'Y' TO WS-FIRST-RECORD
           MOVE ZEROS TO WS-SUBTOTALS
           MOVE ZEROS TO WS-PAGE-CONTROL
           MOVE SPACES TO WS-PREV-REGION WS-PREV-DEPT.

       2000-READ-PARAMETERS.
           READ PARAMETER-FILE INTO PARM-RECORD
               AT END MOVE 'Y' TO WS-EOF-PARM
           END-READ
           IF WS-EOF-PARM = 'N'
               MOVE PARM-FROM-DATE     TO WS-FROM-DATE
               MOVE PARM-TO-DATE       TO WS-TO-DATE
               MOVE PARM-REGION-FILTER TO WS-REGION-FILTER
               IF PARM-LINES-PER-PG > ZEROS
                   MOVE PARM-LINES-PER-PG TO WS-LINES-PER-PAGE
               END-IF
           ELSE
               MOVE 00000000 TO WS-FROM-DATE
               MOVE 99991231 TO WS-TO-DATE
               MOVE SPACES   TO WS-REGION-FILTER
           END-IF.

       2100-READ-TRANSACTION.
           READ TRANSACTION-FILE INTO TRANS-RECORD
               AT END MOVE 'Y' TO WS-EOF-TRANS
           END-READ.

       3000-PROCESS-RECORD.
           PERFORM 3100-APPLY-DATE-FILTER
           IF WS-IN-RANGE = 'Y'
               PERFORM 3200-APPLY-REGION-FILTER
               IF WS-IN-RANGE = 'Y'
                   MOVE TR-REGION-CODE TO WS-CURR-REGION
                   MOVE TR-DEPT-CODE   TO WS-CURR-DEPT
                   PERFORM 3300-DETECT-BREAKS
                   IF WS-FIRST-RECORD = 'N'
                       IF WS-REGION-BREAK = 'Y'
                           PERFORM 3410-PRINT-DEPT-SUBTOTAL
                           PERFORM 3420-PRINT-REGION-SUBTOTAL
                       ELSE
                           IF WS-DEPT-BREAK = 'Y'
                               PERFORM 3410-PRINT-DEPT-SUBTOTAL
                           END-IF
                       END-IF
                   END-IF
                   IF WS-FIRST-RECORD = 'Y' OR
                      WS-REGION-BREAK = 'Y'
                       PERFORM 3500-PRINT-REGION-HEADER
                   END-IF
                   IF WS-DEPT-BREAK = 'Y' OR
                      WS-FIRST-RECORD = 'Y'
                       PERFORM 3510-PRINT-DEPT-HEADER
                   END-IF
                   MOVE 'N' TO WS-FIRST-RECORD
                   PERFORM 3600-PRINT-DETAIL-LINE
                   ADD TR-AMOUNT TO WS-DEPT-TOTAL
                   ADD TR-AMOUNT TO WS-REGION-TOTAL
                   ADD TR-AMOUNT TO WS-GRAND-TOTAL
                   ADD 1 TO WS-DEPT-COUNT
                   ADD 1 TO WS-REGION-COUNT
                   ADD 1 TO WS-GRAND-COUNT
                   MOVE WS-CURR-REGION TO WS-PREV-REGION
                   MOVE WS-CURR-DEPT   TO WS-PREV-DEPT
               ELSE
                   ADD 1 TO WS-FILTERED-COUNT
               END-IF
           ELSE
               ADD 1 TO WS-FILTERED-COUNT
           END-IF
           PERFORM 2100-READ-TRANSACTION.

       3100-APPLY-DATE-FILTER.
           MOVE 'N' TO WS-IN-RANGE
           IF TR-TRANS-DATE >= WS-FROM-DATE AND
              TR-TRANS-DATE <= WS-TO-DATE
               MOVE 'Y' TO WS-IN-RANGE
           END-IF.

       3200-APPLY-REGION-FILTER.
           IF WS-REGION-FILTER NOT = SPACES
               IF TR-REGION-CODE NOT = WS-REGION-FILTER
                   MOVE 'N' TO WS-IN-RANGE
               END-IF
           END-IF.

       3300-DETECT-BREAKS.
           MOVE 'N' TO WS-REGION-BREAK
           MOVE 'N' TO WS-DEPT-BREAK
           IF WS-CURR-REGION NOT = WS-PREV-REGION
               MOVE 'Y' TO WS-REGION-BREAK
               MOVE 'Y' TO WS-DEPT-BREAK
           ELSE
               IF WS-CURR-DEPT NOT = WS-PREV-DEPT
                   MOVE 'Y' TO WS-DEPT-BREAK
               END-IF
           END-IF.

       3410-PRINT-DEPT-SUBTOTAL.
           MOVE SPACES TO RPT-LINE
           WRITE RPT-LINE
           MOVE 'DEPT TOTAL: ' TO WS-SL-LABEL
           MOVE WS-DEPT-COUNT TO WS-SL-COUNT
           MOVE WS-DEPT-TOTAL TO WS-SL-AMOUNT
           MOVE WS-SUBTOTAL-LINE TO RPT-LINE
           WRITE RPT-LINE
           MOVE ZEROS TO WS-DEPT-TOTAL WS-DEPT-COUNT.

       3420-PRINT-REGION-SUBTOTAL.
           MOVE '** REGION TOTAL: ' TO WS-SL-LABEL
           MOVE WS-REGION-COUNT TO WS-SL-COUNT
           MOVE WS-REGION-TOTAL TO WS-SL-AMOUNT
           MOVE WS-SUBTOTAL-LINE TO RPT-LINE
           WRITE RPT-LINE
           MOVE ZEROS TO WS-REGION-TOTAL WS-REGION-COUNT
           MOVE SPACES TO RPT-LINE
           WRITE RPT-LINE.

       3500-PRINT-REGION-HEADER.
           PERFORM 3700-CHECK-PAGE-BREAK
           MOVE SPACES TO RPT-LINE
           STRING 'REGION: ' WS-CURR-REGION
               DELIMITED BY SIZE INTO RPT-LINE
           WRITE RPT-LINE
           ADD 1 TO WS-LINES-USED.

       3510-PRINT-DEPT-HEADER.
           MOVE SPACES TO RPT-LINE
           STRING '  DEPARTMENT: ' WS-CURR-DEPT
               DELIMITED BY SIZE INTO RPT-LINE
           WRITE RPT-LINE
           MOVE SPACES TO RPT-LINE
           MOVE WS-HEADER-LINE-2 TO RPT-LINE
           WRITE RPT-LINE
           ADD 2 TO WS-LINES-USED.

       3600-PRINT-DETAIL-LINE.
           PERFORM 3700-CHECK-PAGE-BREAK
           MOVE TR-TRANS-DATE   TO WS-DL-DATE
           MOVE TR-ACCOUNT-CODE TO WS-DL-ACCOUNT
           MOVE TR-DESCRIPTION  TO WS-DL-DESC
           MOVE TR-TRANS-TYPE   TO WS-DL-TYPE
           MOVE TR-AMOUNT       TO WS-DL-AMOUNT
           MOVE TR-REFERENCE    TO WS-DL-REF
           MOVE WS-DETAIL-LINE  TO RPT-LINE
           WRITE RPT-LINE
           ADD 1 TO WS-LINES-USED.

       3700-CHECK-PAGE-BREAK.
           IF WS-LINES-USED >= WS-LINES-PER-PAGE
               ADD 1 TO WS-PAGE-NUMBER
               MOVE SPACES TO RPT-LINE
               WRITE RPT-LINE AFTER ADVANCING PAGE
               MOVE WS-FROM-DATE  TO WS-HDR-FROM
               MOVE WS-TO-DATE    TO WS-HDR-TO
               MOVE WS-PAGE-NUMBER TO WS-HDR-PAGE
               MOVE WS-HEADER-LINE-1 TO RPT-LINE
               WRITE RPT-LINE
               MOVE 2 TO WS-LINES-USED
           END-IF.

       4000-FINAL-TOTALS.
           IF WS-FIRST-RECORD = 'N'
               PERFORM 3410-PRINT-DEPT-SUBTOTAL
               PERFORM 3420-PRINT-REGION-SUBTOTAL
           END-IF
           MOVE SPACES TO RPT-LINE
           WRITE RPT-LINE
           MOVE '*** GRAND TOTAL' TO WS-SL-LABEL
           MOVE WS-GRAND-COUNT TO WS-SL-COUNT
           MOVE WS-GRAND-TOTAL TO WS-SL-AMOUNT
           MOVE WS-SUBTOTAL-LINE TO RPT-LINE
           WRITE RPT-LINE
           MOVE SPACES TO RPT-LINE
           STRING 'RECORDS FILTERED OUT: ' WS-FILTERED-COUNT
               DELIMITED BY SIZE INTO RPT-LINE
           WRITE RPT-LINE.

       9000-TERMINATE.
           CLOSE TRANSACTION-FILE
           CLOSE PARAMETER-FILE
           CLOSE REPORT-OUTPUT.
