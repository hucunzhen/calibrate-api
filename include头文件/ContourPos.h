
#ifndef	CONTOURPOS_H
#define	CONTOURPOS_H

#ifdef ALGORITHM_EXPORTS
#define DLLEXPORT_API extern "C" _declspec(dllexport)
#else
#define DLLEXPORT_API extern "C" _declspec(dllimport)
#endif

#include "XVBase.h"
namespace XVL
{
	///////////////////////信号定义
	#define TOOL_PASS                                         0x0001         //通过
	#define TOOL_FAIL_No_LearnResult                          0x0000         //无学习信息
	#define TOOL_Learn_Success                                0x0001         //学习成功
	#define TOOL_FAIL_No_Target                               0x0000         //无目标
	///////////////////////
	 enum XVTransformType
	{
		affine,                  //仿射变换
		position_rotation_scale, //旋转加缩放
		position_rotation,       //旋转加位置
	};

	typedef struct XVContourRectangle2D
	{
		XVPoint2DInt                  firstPoint;
		XVPoint2DInt                  secondPoint;
		XVPoint2DInt                  thirdPoint;
	}XVContourRectangle2D;

	typedef struct CalculateParam
	{
		BYTE                                  *AngleTable;    //角度查询表512*512
		short                                 *GradientTable; //梯度查询表
		CalculateParam()
		{
			AngleTable = 0;
			GradientTable = 0;
		}
	}CalculateParam;


	typedef struct MappedProbeBitInfo
	{
		int                                        numOfProbe;
		int                                        numOfScale;
		int                                        numOfRotation;
		int*                                       mappedProbe_x;           //List
		int*                                       mappedProbe_y;           //List
		int*                                       mappedProbe_index;       //List
		short*                                     mappedProbe_angleBit;    //List
		int*                                       mappedProbe_MER;         //List
		int*                                       preScaleImageHeight;     //List
		int*                                       preScaleImageWidth;      //List
		int*                                       scaleMappedProbe_MERSize;//List
		int                                        tableForEdgeAngleBit[256];
		int                                        preSampledSearchImageHeight;
		int                                        preSampledSearchImageWidth;
		int                                        rotationIndex[256];
		int                                        rotationNum;
		float                                      probeGradient;
	}MappedProbeBitInfo;

	typedef struct MappedProbeInfo
	{
		int                                        numOfProbe;
		int                                        quantity;
		int*                                       mappedProbe_x;
		int*                                       mappedProbe_y;
		int*                                       mappedProbe_index;
		int*                                       mappedProbe_x_p;
		int*                                       mappedProbe_y_p;
		int*                                       mappedProbe_index_p;
		int*                                       mappedProbe_x_n;
		int*                                       mappedProbe_y_n;
		int*                                       mappedProbe_index_n;
		int*                                       mappedProbe_angle;
		int*                                       mappedProbe_MER;
		int*                                       mappedProbe_w;
		int*                                       mappedProbe_h;
		int*                                       rotateAngle;
		float*                                     tableForMatch;
		float*                                     tableForGMatch;
		int                                        numOfMatchElement;
		int                                        numOfGMatchElement;
		int                                        numOfRotation;
		float                                      cx;
		float                                      cy;
		int                                        preSampledSearchImageHeight;
		int                                        preSampledSearchImageWidth;
		int                                        maxProbeMERSize;
		int*                                       mappedProbe_index2;
		int*                                       mappedProbe_index_p2;
		int*                                       mappedProbe_index_n2;
		int                                        numOffset;
		int*                                       offsetX;
		int*                                       offsetY;
		int*                                       offsetIndex;
	}MappedProbeInfo;

	typedef struct Offset_A_Index_Info
	{
		int                                        numOffset;
		int                                        quantity;
		int*                                       offsetX;
		int*                                       offsetY;
		int*                                       offsetIndex;
	}Offset_A_Index_Info;

	typedef struct DipoleListInfo
	{
		int                                        numOfDipole;
		int                                        quantity;
		int                                        minX;
		int                                        maxX;
		int                                        minY;
		int                                        maxY;
		int*                                       x;
		int*                                       y;
		int*                                       gradientMagnitude;
		int*                                       gradientDirection;
		int*                                       chainIndex;
		int*                                       elementIndex;
		int                                        trainImageHeight;
		int                                        trainImageWidth;
	}DipoleListInfo;

	typedef struct ForceListInfo
	{
		int                                        numOfForce;
		int                                        quantity;
		int*                                       x;
		int*                                       y;
		int*                                       index;
		int*                                       gradientDirection;
		float*                                     forceMagnitude;
		float*                                     unitVectorX;
		float*                                     unitVectorY;
		int*                                       corner;
		int*                                       polarity;
		int*                                       nearestFieldDipole;
		float*                                     tableSin;
		float*                                     tableCos;
		float*                                     tableSin100000;
		float*                                     tableCos100000;
	}ForceListInfo;

	typedef struct ContourPosPatternInfo
	{
		MappedProbeBitInfo                         mappedProbeBit;
		MappedProbeInfo                            mappedProbe;
		Offset_A_Index_Info                        offset_A_Index;
		DipoleListInfo                             dipoleListCR;
		DipoleListInfo                             dipoleListLR;
		DipoleListInfo                             dipoleListHR;
		ForceListInfo                              forceListCR;
		ForceListInfo                              forceListLR;
		ForceListInfo                              forceListHR;
		int*                                       indexOfForceCR;
		int*                                       indexOfForceLR;
		int*                                       indexOfForceHR;
	}ContourPosPatternInfo;

	typedef struct ChainObj
	{
		int                                          chainLength;
		vector<XVPoint2D>                            chainPoints;
	}ChainObj;

	typedef struct ContourPosResultInfo
	{
		float                                           angle;
		float                                           score;
		float                                           scale;
		float                                           fineScore;
		XVRectangle2D			                        match;                  //矩形框
		XVCoordinateSystem2D	                        alignment;              //alignment (数据不显示)

		XVPoint2D                                       center;
		int                                             featerPointNum;
		vector<XVPoint2DInt>                            featerPoints;           //点显示
		int                                             targetPointNum;
		vector<XVPoint2D>                               targetPoints;           //模型变换之后的点
		int                                             objectEdgePointNum;
		vector<XVPoint2D>                               objectEdgePoints;       //待测图目标边缘点
		vector<float>                                   objectEdgePointEvalues; //待测图目标边缘点权值

		float                                           map[8];
		int                                             chainNum;
		vector<ChainObj>                                chains;                 //链条 显示绿色
		int                                             defectChainNum;
		vector<ChainObj>                                defectChains;           //瑕疵链条 显示红色
	}ContourPosResultInfo;

	typedef struct ContourPosLearnIn
	{
		XVImage*                                        inImage;                       //输入图像
		XVRegion*                                       inTemplateRegion;              //ROI擦除区域（NIL时learnRegion区域参与模型的生成）
		XVContourRectangle2D                            learnRegion;                   //学习矩形区域 

		int                                             learnSignal;                   //学习信号 1表示学习 0表示不学习
		int                                             sampleRate;                    //采样系数 4(2 4)
		int                                             pyramidNum;                    //采样层数 2(2 3 4 5)
		int                                             gradientThreshold;             //对比度阈值 20 (>=5)
		int                                             coarsePatternPrecision;        //粗定位模型精度 48 (>=30)

		int                                             isFineLearnSignal;             //是否精定位 1
		int                                             finePatternPrecision;          //精定位模型精度 30(>=1)
		int                                             finePatternGradientThreshold;  //精定位对比度阈值 20 (>=5)
	}ContourPosLearnIn;

	typedef struct ContourPosLearnOut
	{
		XVContourRectangle2D                            learnRegion;                   //用于计算alignment;
		int                                             learnResult;
		int                                             fineLearnResult;
		int                                             learnRect_firstPointx;
		int                                             learnRect_firstPointy;
		int                                             sampleRate;
		int                                             pyramidNum;
		int                                             finePatternPrecision;
		int                                             gradientThreshold;
		int                                             finePatternGradientThreshold;
		CalculateParam                                  calculateParam;
		ContourPosPatternInfo                           patternInfo;
		vector<XVPoint2D>                               featerPoints;           //粗定位特征点 只有粗定位的时候显示
		vector<XVPoint2D>                               edgePoints;
		XVPoint2D                                       patternOrigin;//模板中心
		vector<XVPath>                                  edgeChains;   //模板链条
		vector<int>                                     learnOutChange;   //模板发生变化
		vector<int>                                     loadModelChange;
		int                                             minGradient; //最小对比度阈值
		int                                             maxGradient; //最大对比度阈值
		int                                             avgGradient; //平均对比度阈值
	}ContourPosLearnOut;

	typedef struct ContourPosMatchIn
	{
		XVImage*                                        inImage;                 //输入图像
		XVRegion*                                       inSearchRegion;          //(默认值0 有效时将optional置成ENABLE)
		XVContourRectangle2D                            detectRegion;            //检测区域 默认值全图

		int                                             gradientThreshold;       //对比度阈值 20
		int                                             targetNum;               //目标数0
		int                                             similarityThreshold;     //相似度阈值 60
		int                                             startAngle;              //起始角度 -45
		int                                             endAngle;                //终止角度 45
		int                                             overlapPara;             //重叠系数 40(>=20)

		BOOL                                            isFineLocalization;           //是否精定位 1
		XVTransformType                                 inTransformType;              //变换类型 1
		int                                             finePatternGradientThreshold; //精定位对比度阈值 20
		int                                             attractionNum;                //迭代次数5
		int                                             fineSimilarityThreshold;      //精定位相似度阈值 60
		int                                             testImgDipoleListSampleRate;  //目标图像边缘采样率1 (1-30)

		BOOL                                            isDefectDetect;               //是否缺陷检测0
		float                                           defectDetectPrecision;        //检测精度0.1
		int                                             maxDefectChainLength;         //最大瑕疵链长 默认值100000

		//预设内存空间
		int                                             preImageWidth;   //初始化 0
		int                                             preImageHeight;  //0
		BYTE*                                           searchImageCR;   //0
		BYTE*                                           angleImageCR;    //0
		short*                                          gradientImageCR; //0 
		BYTE*                                           searchImageLR;   //0
		BYTE*                                           angleImageLR;    //0
		short*                                          gradientImageLR; //0 
		BYTE*                                           filterdImageHR;  //0
		BYTE*                                           angleImageHR;    //0
		short*                                          gradientImageHR; //0 
	}ContourPosMatchIn;

	typedef struct ContourPosMatchOut
	{
		int                                          result;
		float                                        time;//执行时间
		XVContourRectangle2D                         detectRegion;
		vector<ContourPosResultInfo>                 results;
	}ContourPosMatchOut;

	typedef struct ContourPosDebugInfoIn
	{
		int                        detectType;//暂时参数
	}ContourPosDebugInfoIn;

	typedef struct ContourPosDebugInfoOut
	{
		int                        result;
		XVImage*                   coarseMatchGradientImg; //粗定位梯度图像
	}ContourPosDebugInfoOut;

	typedef struct ContourPos
	{
		ContourPosLearnIn                               lin;
		ContourPosLearnOut                              lout;
		ContourPosMatchIn                               min;
		ContourPosMatchOut                              mout;
	}ContourPos;

	DLLEXPORT_API void  ContourPos_InitializePattern(ContourPosLearnOut *contourPosLearn_Out);
	DLLEXPORT_API void  ContourPos_DeletePattern(ContourPosLearnOut *contourPosLearn_Out);
	DLLEXPORT_API void  ContourPos_InitializeMatchIn(ContourPosMatchIn *contourPosMatch_In);
	DLLEXPORT_API void  ContourPos_DeleteMatchIn(ContourPosMatchIn *contourPosMatch_In);

	DLLEXPORT_API void  ContourPos_Learn(ContourPosLearnIn *contourPosLearn_In, ContourPosLearnOut *contourPosLearn_Out);
	DLLEXPORT_API void  ContourPos_Match(ContourPosMatchIn *contourPosMatch_In, ContourPosLearnOut *contourPosLearn_Out, ContourPosMatchOut *contourPosMatch_Out);
	DLLEXPORT_API void  ContourPos_DebugInfomation(ContourPosMatchIn *contourPosMatch_In, ContourPosLearnOut *contourPosLearn_Out, ContourPosDebugInfoIn *ContourPosDebugInfo_In, ContourPosDebugInfoOut *ContourPosDebugInfo_Out);

	DLLEXPORT_API void  ContourPos_SavePattern(ContourPosLearnOut* contourPosLearn_Out, wchar_t fileName[]);
	DLLEXPORT_API void  ContourPos_LoadPattern(ContourPosLearnOut* contourPosLearn_Out, wchar_t fileName[]);

	////////
	//DLLEXPORT_API void  ContourPos_Learn_Test(ContourPosLearnIn *contourPosLearn_In, ContourPosLearnOut *contourPosLearn_Out);
	/////////CAD
	typedef struct ContourPosCADChainLearnIn
	{
		XVImage*                                        inImage;        //输入图像
		XVRegion*                                       inTemplateRegion; //ROI擦除区域（NIL时learnRegion区域参与模型的生成）
		XVContourRectangle2D                            learnRegion;      //学习区域 

		int                                             chainLengthThreshold; //链长阈值 30
		int                                             gradientThreshold;    //梯度阈值 60
	}ContourPosCADChainLearnIn;

	typedef struct ContourPosCADChainLearnOut
	{
		int                                             result; //1成功 -1失败
		vector<ChainObj>                                edgeChains;
	}ContourPosCADChainLearnOut;

	typedef struct SegmentInfoCAD
	{
		int    chainLength; //原始链数据
		vector<float> subX;
		vector<float> subY;

		int          segmentNum;  //拟合成的CAD直线段和圆弧段
		vector<int>    segmentType;
		vector<int>    startPointIndex;
		vector<int>    endPointIndex;
		vector<float>  startPointx;
		vector<float>  startPointy;
		vector<float>  endPointx;
		vector<float>  endPointy;
		vector<float>  arcCenterx;
		vector<float>  arcCentery;
		vector<float>  R;
		vector<float>  midArcPoint_x;
		vector<float>  midArcPoint_y;
		vector<float>  startAngle; //起始角度[0,360)
		vector<float>  sweepAngle; //扫描过的角度[-360,360]

	}SegmentInfoCAD;

	typedef struct ContourPosCADSegmentLearnIn
	{
		float maxDeviationThresholdForLine;    //直线拟合精度 1
		float maxDeviationThresholdForArc;     //圆弧拟合精度 1
		float deviationThresholdForArcANDLine; //圆弧和直线相对精度 0.4

	}ContourPosCADSegmentLearnIn;

	typedef struct ContourPosCADSegmentLearnOut
	{
		int                                          result;
		vector<SegmentInfoCAD>                       segmentsCAD;
	}ContourPosCADSegmentLearnOut;
	DLLEXPORT_API void  ContourPosCAD_ChainLearn(ContourPosCADChainLearnIn *contourPosCADChainLearnIn, ContourPosCADChainLearnOut *contourPosCADChainLearnOut);
	DLLEXPORT_API void  ContourPosCAD_SegmentLearn(ContourPosCADSegmentLearnIn *contourPosCADSegmentLearnIn, ContourPosCADChainLearnOut *contourPosCADChainLearnOut, ContourPosCADSegmentLearnOut *contourPosCADSegmentLearnOut);
}
#endif